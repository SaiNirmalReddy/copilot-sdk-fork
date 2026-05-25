use std::sync::Arc;
use std::time::Duration;

use async_trait::async_trait;
use github_copilot_sdk::Error;
use github_copilot_sdk::canvas::{
    CanvasDeclaration, CanvasHandler, CanvasOpenContext, CanvasOpenResponse, CanvasResult,
};
use github_copilot_sdk::generated::api_types::{
    CanvasAction, CanvasCloseRequest, CanvasInstanceAvailability, CanvasInvokeActionRequest,
    CanvasOpenRequest,
};
use github_copilot_sdk::types::{ExtensionInfo, ResumeSessionConfig};
use parking_lot::Mutex;
use serde_json::{Value, json};

use super::support::{DEFAULT_TEST_TOKEN, with_e2e_context};

#[derive(Debug, PartialEq)]
struct OpenCall {
    canvas_id: String,
    instance_id: String,
    input: Value,
}

#[derive(Debug, PartialEq)]
struct ActionCall {
    action_name: String,
    instance_id: String,
    input: Value,
}

#[derive(Debug, PartialEq)]
struct CloseCall {
    canvas_id: String,
    instance_id: String,
}

#[derive(Default)]
struct CanvasCalls {
    opens: Mutex<Vec<OpenCall>>,
    actions: Mutex<Vec<ActionCall>>,
    closes: Mutex<Vec<CloseCall>>,
}

struct CounterHandler {
    calls: Arc<CanvasCalls>,
}

#[async_trait]
impl CanvasHandler for CounterHandler {
    async fn on_open(&self, ctx: CanvasOpenContext) -> CanvasResult<CanvasOpenResponse> {
        record_open(&self.calls, &ctx);
        Ok(CanvasOpenResponse {
            url: Some(format!("https://example.test/{}", ctx.instance_id)),
            title: None,
            status: None,
        })
    }

    async fn on_action(
        &self,
        ctx: github_copilot_sdk::canvas::CanvasActionContext,
    ) -> CanvasResult<Value> {
        self.calls.actions.lock().push(ActionCall {
            action_name: ctx.action_name.clone(),
            instance_id: ctx.instance_id,
            input: ctx.input.clone(),
        });
        Ok(json!({
            "ok": true,
            "actionName": ctx.action_name,
            "input": ctx.input,
        }))
    }

    async fn on_close(
        &self,
        ctx: github_copilot_sdk::canvas::CanvasLifecycleContext,
    ) -> CanvasResult<()> {
        self.calls.closes.lock().push(CloseCall {
            canvas_id: ctx.canvas_id,
            instance_id: ctx.instance_id,
        });
        Ok(())
    }
}

struct OpenOnlyHandler {
    calls: Arc<CanvasCalls>,
}

#[async_trait]
impl CanvasHandler for OpenOnlyHandler {
    async fn on_open(&self, ctx: CanvasOpenContext) -> CanvasResult<CanvasOpenResponse> {
        record_open(&self.calls, &ctx);
        Ok(CanvasOpenResponse {
            url: Some(format!("https://example.test/{}", ctx.instance_id)),
            title: None,
            status: None,
        })
    }
}

#[tokio::test]
async fn dispatches_canvas_open_to_the_provider_handler() {
    with_e2e_context(
        "canvas",
        "dispatches_canvas_open_to_the_provider_handler",
        |ctx| {
            Box::pin(async move {
                ctx.set_default_copilot_user();
                let calls = Arc::new(CanvasCalls::default());
                let client = ctx.start_client().await;
                let session = client
                    .create_session(canvas_session_config(Arc::new(CounterHandler {
                        calls: calls.clone(),
                    })))
                    .await
                    .expect("create session");

                let result = session
                    .rpc()
                    .canvas()
                    .open(CanvasOpenRequest {
                        canvas_id: "counter".to_string(),
                        extension_id: None,
                        input: Some(json!({ "seed": 7 })),
                        instance_id: "counter-1".to_string(),
                    })
                    .await
                    .expect("open canvas");

                assert_eq!(
                    calls.opens.lock().as_slice(),
                    [OpenCall {
                        canvas_id: "counter".to_string(),
                        instance_id: "counter-1".to_string(),
                        input: json!({ "seed": 7 }),
                    }]
                );
                assert_eq!(result.canvas_id, "counter");
                assert_eq!(result.instance_id, "counter-1");
                assert_eq!(
                    result.url.as_deref(),
                    Some("https://example.test/counter-1")
                );
                assert_eq!(result.availability, CanvasInstanceAvailability::Ready);

                session.disconnect().await.expect("disconnect session");
                client.stop().await.expect("stop client");
            })
        },
    )
    .await;
}

#[tokio::test]
async fn dispatches_canvas_action_invoke_to_the_per_action_handler() {
    with_e2e_context(
        "canvas",
        "dispatches_canvas_action_invoke_to_the_per_action_handler",
        |ctx| {
            Box::pin(async move {
                ctx.set_default_copilot_user();
                let calls = Arc::new(CanvasCalls::default());
                let client = ctx.start_client().await;
                let session = client
                    .create_session(canvas_session_config(Arc::new(CounterHandler {
                        calls: calls.clone(),
                    })))
                    .await
                    .expect("create session");

                session
                    .rpc()
                    .canvas()
                    .open(CanvasOpenRequest {
                        canvas_id: "counter".to_string(),
                        extension_id: None,
                        input: None,
                        instance_id: "counter-2".to_string(),
                    })
                    .await
                    .expect("open canvas");
                let result = session
                    .rpc()
                    .canvas()
                    .invoke_action(CanvasInvokeActionRequest {
                        action_name: "increment".to_string(),
                        input: Some(json!({ "amount": 3 })),
                        instance_id: "counter-2".to_string(),
                    })
                    .await
                    .expect("invoke action");

                assert_eq!(
                    calls.actions.lock().as_slice(),
                    [ActionCall {
                        action_name: "increment".to_string(),
                        instance_id: "counter-2".to_string(),
                        input: json!({ "amount": 3 }),
                    }]
                );
                assert_eq!(
                    result.result,
                    Some(json!({
                        "ok": true,
                        "actionName": "increment",
                        "input": { "amount": 3 },
                    }))
                );

                session.disconnect().await.expect("disconnect session");
                client.stop().await.expect("stop client");
            })
        },
    )
    .await;
}

#[tokio::test]
async fn dispatches_canvas_close_to_the_provider_on_close_handler() {
    with_e2e_context(
        "canvas",
        "dispatches_canvas_close_to_the_provider_on_close_handler",
        |ctx| {
            Box::pin(async move {
                ctx.set_default_copilot_user();
                let calls = Arc::new(CanvasCalls::default());
                let client = ctx.start_client().await;
                let session = client
                    .create_session(canvas_session_config(Arc::new(CounterHandler {
                        calls: calls.clone(),
                    })))
                    .await
                    .expect("create session");

                session
                    .rpc()
                    .canvas()
                    .open(CanvasOpenRequest {
                        canvas_id: "counter".to_string(),
                        extension_id: None,
                        input: None,
                        instance_id: "counter-3".to_string(),
                    })
                    .await
                    .expect("open canvas");
                session
                    .rpc()
                    .canvas()
                    .close(CanvasCloseRequest {
                        instance_id: "counter-3".to_string(),
                    })
                    .await
                    .expect("close canvas");
                tokio::time::sleep(Duration::from_millis(50)).await;

                assert_eq!(
                    calls.closes.lock().as_slice(),
                    [CloseCall {
                        canvas_id: "counter".to_string(),
                        instance_id: "counter-3".to_string(),
                    }]
                );

                session.disconnect().await.expect("disconnect session");
                client.stop().await.expect("stop client");
            })
        },
    )
    .await;
}

#[tokio::test]
async fn returns_canvas_action_no_handler_when_the_declared_action_has_no_handler() {
    with_e2e_context(
        "canvas",
        "returns_canvas_action_no_handler_when_the_declared_action_has_no_handler",
        |ctx| {
            Box::pin(async move {
                ctx.set_default_copilot_user();
                let client = ctx.start_client().await;
                let session = client
                    .create_session(canvas_session_config(Arc::new(OpenOnlyHandler {
                        calls: Arc::new(CanvasCalls::default()),
                    })))
                    .await
                    .expect("create session");

                session
                    .rpc()
                    .canvas()
                    .open(CanvasOpenRequest {
                        canvas_id: "counter".to_string(),
                        extension_id: None,
                        input: None,
                        instance_id: "counter-4".to_string(),
                    })
                    .await
                    .expect("open canvas");
                let err = session
                    .rpc()
                    .canvas()
                    .invoke_action(CanvasInvokeActionRequest {
                        action_name: "increment".to_string(),
                        input: Some(json!({})),
                        instance_id: "counter-4".to_string(),
                    })
                    .await
                    .expect_err("invoke action should fail");

                assert_rpc_error_contains(&err, "No handler implemented for this canvas action");

                session.disconnect().await.expect("disconnect session");
                client.stop().await.expect("stop client");
            })
        },
    )
    .await;
}

#[tokio::test]
async fn seeds_open_canvases_on_resume_from_the_runtime_resume_response() {
    with_e2e_context(
        "canvas",
        "seeds_open_canvases_on_resume_from_the_runtime_resume_response",
        |ctx| {
            Box::pin(async move {
                ctx.set_default_copilot_user();
                let client = ctx.start_client().await;
                let session = client
                    .create_session(canvas_session_config(Arc::new(CounterHandler {
                        calls: Arc::new(CanvasCalls::default()),
                    })))
                    .await
                    .expect("create session");

                session
                    .rpc()
                    .canvas()
                    .open(CanvasOpenRequest {
                        canvas_id: "counter".to_string(),
                        extension_id: None,
                        input: Some(json!({ "initial": true })),
                        instance_id: "counter-resume".to_string(),
                    })
                    .await
                    .expect("open canvas");

                let resumed = client
                    .resume_session(
                        ResumeSessionConfig::new(session.id().clone())
                            .with_canvases([counter_canvas()])
                            .with_canvas_handler(Arc::new(CounterHandler {
                                calls: Arc::new(CanvasCalls::default()),
                            }))
                            .with_request_canvas_renderer(true)
                            .with_extension_info(extension_info())
                            .with_github_token(DEFAULT_TEST_TOKEN),
                    )
                    .await
                    .expect("resume session");

                let seeded = resumed.open_canvases();
                assert!(
                    seeded
                        .iter()
                        .any(|canvas| canvas.instance_id == "counter-resume"
                            && canvas.canvas_id == "counter")
                );

                resumed
                    .disconnect()
                    .await
                    .expect("disconnect resumed session");
                session.stop_event_loop().await;
                client.stop().await.expect("stop client");
            })
        },
    )
    .await;
}

fn record_open(calls: &CanvasCalls, ctx: &CanvasOpenContext) {
    calls.opens.lock().push(OpenCall {
        canvas_id: ctx.canvas_id.clone(),
        instance_id: ctx.instance_id.clone(),
        input: ctx.input.clone(),
    });
}

fn canvas_session_config(handler: Arc<dyn CanvasHandler>) -> github_copilot_sdk::SessionConfig {
    github_copilot_sdk::SessionConfig::default()
        .with_permission_handler(Arc::new(github_copilot_sdk::handler::ApproveAllHandler))
        .with_github_token(DEFAULT_TEST_TOKEN)
        .with_canvases([counter_canvas()])
        .with_canvas_handler(handler)
        .with_request_canvas_renderer(true)
        .with_extension_info(extension_info())
}

fn counter_canvas() -> CanvasDeclaration {
    let mut canvas = CanvasDeclaration::new("counter", "Counter", "A test counter canvas");
    canvas.actions = Some(vec![CanvasAction {
        name: "increment".to_string(),
        description: Some("Increment the counter".to_string()),
        input_schema: None,
    }]);
    canvas
}

fn extension_info() -> ExtensionInfo {
    ExtensionInfo::new("github-app", "counter-provider")
}

fn assert_rpc_error_contains(err: &Error, expected: &str) {
    match err {
        Error::Rpc { message, .. } => assert!(
            message.contains(expected),
            "expected RPC error message to contain {expected:?}, got {message:?}"
        ),
        other => panic!("expected RPC error, got {other:?}"),
    }
}
