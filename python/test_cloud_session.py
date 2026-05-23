"""
Tests for CopilotClient.create_cloud_session.

Ports the spirit of the Rust integration tests in rust/tests/session_test.rs.
"""

from __future__ import annotations

import asyncio
from datetime import datetime
from uuid import uuid4

import pytest

from copilot import CopilotClient, RuntimeConnection
from copilot.client import CloudSessionOptions, CloudSessionRepository
from copilot.session import ProviderConfig, UserInputRequest, UserInputResponse
from e2e.testharness import CLI_PATH

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _cloud_config() -> dict:
    return dict(
        cloud=CloudSessionOptions(
            repository=CloudSessionRepository(owner="github", name="copilot-sdk", branch="main")
        )
    )


def _make_event_dict(event_type: str = "session.buffered_test", data: dict | None = None) -> dict:
    """Build a minimal valid session-event dict for injection in tests."""
    return {
        "id": str(uuid4()),
        "timestamp": datetime.now().isoformat(),
        "parentId": None,
        "type": event_type,
        "data": data or {},
    }


# ---------------------------------------------------------------------------
# Test 1: create_session rejects cloud config
# ---------------------------------------------------------------------------


class TestCreateSessionRejectsCloud:
    @pytest.mark.asyncio
    async def test_create_session_rejects_cloud_config(self):
        """create_session must raise ValueError mentioning create_cloud_session."""
        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        await client.start()
        try:
            with pytest.raises(ValueError, match="create_cloud_session"):
                await client.create_session(**_cloud_config())
        finally:
            await client.force_stop()


# ---------------------------------------------------------------------------
# Test 2: wire shape — sessionId omitted, cloud set, returned id used
# ---------------------------------------------------------------------------


class TestCreateCloudSessionWireShape:
    @pytest.mark.asyncio
    async def test_sends_cloud_without_session_id(self):
        """session.create must carry cloud but omit sessionId; the response id is used."""
        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        await client.start()
        try:
            captured: dict = {}

            async def mock_request(method, params):
                captured[method] = params
                if method == "session.create":
                    return {
                        "sessionId": "remote-cloud-session",
                        "remoteUrl": "https://copilot.example.test/agents/remote-cloud-session",
                        "capabilities": {"ui": {"elicitation": True}},
                    }
                return {}

            client._client.request = mock_request
            session = await client.create_cloud_session(**_cloud_config())

            wire = captured["session.create"]
            assert "sessionId" not in wire, "sessionId must be omitted from cloud create"
            assert wire["cloud"]["repository"]["owner"] == "github"
            assert wire["cloud"]["repository"]["name"] == "copilot-sdk"
            assert wire["cloud"]["repository"]["branch"] == "main"
            assert "provider" not in wire

            assert session.session_id == "remote-cloud-session"
            assert session.remote_url == "https://copilot.example.test/agents/remote-cloud-session"
            assert session.capabilities.get("ui", {}).get("elicitation") is True
        finally:
            await client.force_stop()


# ---------------------------------------------------------------------------
# Test 3: rejects caller-provided session_id
# ---------------------------------------------------------------------------


class TestCreateCloudSessionRejectsSessionId:
    @pytest.mark.asyncio
    async def test_rejects_caller_session_id(self):
        """Passing session_id must raise ValueError naming session_id."""
        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        with pytest.raises(ValueError, match="session_id"):
            await client.create_cloud_session(**_cloud_config(), session_id="caller-id")


# ---------------------------------------------------------------------------
# Test 4: rejects caller-provided provider
# ---------------------------------------------------------------------------


class TestCreateCloudSessionRejectsProvider:
    @pytest.mark.asyncio
    async def test_rejects_caller_provider(self):
        """Passing provider must raise ValueError naming provider."""
        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        with pytest.raises(ValueError, match="provider"):
            await client.create_cloud_session(
                **_cloud_config(),
                provider=ProviderConfig(type="openai", base_url="https://api.example.test/v1"),
            )


# ---------------------------------------------------------------------------
# Test 5: requires cloud
# ---------------------------------------------------------------------------


class TestCreateCloudSessionRequiresCloud:
    @pytest.mark.asyncio
    async def test_requires_cloud(self):
        """Omitting cloud (or passing None) must raise ValueError mentioning cloud."""
        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        with pytest.raises(ValueError, match="cloud"):
            await client.create_cloud_session()


# ---------------------------------------------------------------------------
# Test 6: buffers early session.event notifications
# ---------------------------------------------------------------------------


class TestCreateCloudSessionBuffersEarlyNotifications:
    @pytest.mark.asyncio
    async def test_early_notifications_dispatched_after_registration(self):
        """session.event notifications arriving before registration are buffered and replayed."""
        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        await client.start()
        try:
            create_response_gate: asyncio.Future[dict] = asyncio.get_event_loop().create_future()

            async def mock_request(method, params):
                if method == "session.create":
                    return await create_response_gate
                return {}

            client._client.request = mock_request

            session_id = "remote-cloud-session"
            received_events: list = []

            create_task = asyncio.ensure_future(
                client.create_cloud_session(
                    **_cloud_config(),
                    on_event=lambda e: received_events.append(e),
                )
            )

            # Yield control so create_cloud_session enters pending-routing mode.
            await asyncio.sleep(0)
            await asyncio.sleep(0)

            # Inject a session.event notification while the create is in flight.
            notification_handler = client._client.notification_handler
            assert notification_handler is not None, "notification handler not registered"
            notification_handler(
                "session.event",
                {
                    "sessionId": session_id,
                    "event": _make_event_dict(),
                },
            )

            # Verify it is buffered (not yet dispatched — session not registered yet).
            await asyncio.sleep(0)
            assert not received_events, "event dispatched before session was registered"

            # Allow session.create to respond; this registers the session.
            create_response_gate.set_result({"sessionId": session_id})
            await asyncio.wait_for(create_task, timeout=5.0)

            # Give the event loop a tick to flush the buffered event.
            await asyncio.sleep(0)

            assert len(received_events) == 1, (
                f"expected 1 buffered event to be replayed, got {len(received_events)}"
            )
            # Our synthetic event uses an unknown type; just confirm it was dispatched.
            assert received_events[0].raw_type == "session.buffered_test"
        finally:
            await client.force_stop()


# ---------------------------------------------------------------------------
# Test 7: parks inbound requests until registration
# ---------------------------------------------------------------------------


class TestCreateCloudSessionParksInboundRequests:
    @pytest.mark.asyncio
    async def test_parked_user_input_resolves_after_registration(self):
        """userInput.request that arrives before registration is parked, then resolved."""
        answered: list[str] = []

        async def color_picker(request: UserInputRequest, context: dict) -> UserInputResponse:
            answered.append(request["question"])
            return UserInputResponse(answer="blue", wasFreeform=True)

        client = CopilotClient(connection=RuntimeConnection.for_stdio(path=CLI_PATH))
        await client.start()
        try:
            create_response_gate: asyncio.Future[dict] = asyncio.get_event_loop().create_future()

            async def mock_request(method, params):
                if method == "session.create":
                    return await create_response_gate
                return {}

            client._client.request = mock_request

            session_id = "remote-cloud-session"
            create_task = asyncio.ensure_future(
                client.create_cloud_session(**_cloud_config(), on_user_input_request=color_picker)
            )

            # Yield so pending-routing mode is entered.
            await asyncio.sleep(0)
            await asyncio.sleep(0)

            # Dispatch a userInput.request while the create is in flight.
            user_input_handler = client._client.request_handlers.get("userInput.request")
            assert user_input_handler is not None, "userInput.request handler not registered"

            input_task = asyncio.ensure_future(
                user_input_handler(
                    {
                        "sessionId": session_id,
                        "question": "Pick a color",
                        "choices": ["red", "blue"],
                        "allowFreeform": True,
                    }
                )
            )

            # Yield to let the handler park on the pending future.
            await asyncio.sleep(0)
            assert not input_task.done(), "handler should be parked waiting for session"

            # Now let the create response arrive; this registers the session.
            create_response_gate.set_result({"sessionId": session_id})
            await asyncio.wait_for(create_task, timeout=5.0)

            # The parked userInput handler should now complete.
            result = await asyncio.wait_for(input_task, timeout=5.0)
            assert result["answer"] == "blue"
            assert result["wasFreeform"] is True
            assert answered == ["Pick a color"]
        finally:
            await client.force_stop()
