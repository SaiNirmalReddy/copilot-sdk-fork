import { describe, expect, it } from "vitest";
import { buildMcpAppsAllowAttribute, buildMcpAppsCspHeader } from "../src/mcpAppsSandbox.js";

/**
 * SEP-1865 §UI Resource Format → "Restrictive Default" and §Security
 * Implications → "CSP Construction" pin the exact CSP shapes a host MUST emit.
 * These tests pin the spec text to the helper output so any regression is
 * caught against the pinned spec lines, not against an implementation detail.
 */
describe("buildMcpAppsCspHeader", () => {
    it("returns the restrictive default when csp is undefined (spec §UI Resource Format)", () => {
        const header = buildMcpAppsCspHeader(undefined);
        // Restrictive default MUST set connect-src 'none' (no external network).
        expect(header).toContain("default-src 'none'");
        expect(header).toContain("script-src 'self' 'unsafe-inline'");
        expect(header).toContain("style-src 'self' 'unsafe-inline'");
        expect(header).toContain("img-src 'self' data:");
        expect(header).toContain("media-src 'self' data:");
        expect(header).toContain("connect-src 'none'");
        expect(header).toContain("frame-src 'none'");
        expect(header).toContain("object-src 'none'");
        expect(header).toContain("base-uri 'self'");
    });

    it("uses connect-src 'self' (not 'none') when csp is declared with empty arrays", () => {
        // Per spec §Security Implications, a present `csp` block — even with
        // empty arrays — switches to constructed defaults: connect-src 'self'.
        const header = buildMcpAppsCspHeader({});
        expect(header).toContain("connect-src 'self'");
        expect(header).not.toContain("connect-src 'none'");
    });

    it("appends declared connectDomains to connect-src", () => {
        const header = buildMcpAppsCspHeader({
            connectDomains: ["https://api.weather.com", "wss://realtime.service.com"],
        });
        expect(header).toContain(
            "connect-src 'self' https://api.weather.com wss://realtime.service.com"
        );
    });

    it("appends resourceDomains to script-src, style-src, img-src, font-src, media-src", () => {
        const header = buildMcpAppsCspHeader({
            resourceDomains: ["https://cdn.jsdelivr.net"],
        });
        expect(header).toContain("script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net");
        expect(header).toContain("style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net");
        expect(header).toContain("img-src 'self' data: https://cdn.jsdelivr.net");
        expect(header).toContain("font-src 'self' https://cdn.jsdelivr.net");
        expect(header).toContain("media-src 'self' data: https://cdn.jsdelivr.net");
    });

    it("uses declared frameDomains when provided, 'none' otherwise", () => {
        expect(buildMcpAppsCspHeader({})).toContain("frame-src 'none'");
        const header = buildMcpAppsCspHeader({
            frameDomains: ["https://www.youtube.com", "https://player.vimeo.com"],
        });
        expect(header).toContain("frame-src https://www.youtube.com https://player.vimeo.com");
        expect(header).not.toContain("frame-src 'none'");
    });

    it("uses declared baseUriDomains when provided, 'self' otherwise", () => {
        expect(buildMcpAppsCspHeader({})).toContain("base-uri 'self'");
        const header = buildMcpAppsCspHeader({ baseUriDomains: ["https://cdn.example.com"] });
        expect(header).toContain("base-uri https://cdn.example.com");
        expect(header).not.toContain("base-uri 'self'");
    });

    it("always includes object-src 'none' (host MUST block plugins)", () => {
        expect(buildMcpAppsCspHeader(undefined)).toContain("object-src 'none'");
        expect(buildMcpAppsCspHeader({})).toContain("object-src 'none'");
        expect(buildMcpAppsCspHeader({ resourceDomains: ["x"] })).toContain("object-src 'none'");
    });

    // ------------------------------------------------------------------
    // Domain-input sanitization (defends against CSP directive injection
    // from malicious or sloppy MCP servers — see review feedback).
    // ------------------------------------------------------------------

    it("drops entries containing CSP metacharacters that would inject a sibling directive", () => {
        const header = buildMcpAppsCspHeader({
            frameDomains: ["evil.com; form-action *"],
        });
        // The literal injected substring MUST NOT appear in the emitted header.
        expect(header).not.toContain("form-action");
        expect(header).not.toContain(";; ");
        expect(header).not.toContain("evil.com; form-action");
        // With no surviving frameDomains, the directive falls back to 'none'.
        expect(header).toContain("frame-src 'none'");
    });

    it("drops entries containing whitespace, commas, quotes, or backslashes", () => {
        const header = buildMcpAppsCspHeader({
            resourceDomains: [
                "https://ok.example",
                "https://has space.example",
                "https://has,comma.example",
                'https://has"quote.example',
                "https://has\\backslash.example",
                "'self'",
            ],
        });
        expect(header).toContain("https://ok.example");
        expect(header).not.toContain("has space");
        expect(header).not.toContain("has,comma");
        expect(header).not.toContain('has"quote');
        expect(header).not.toContain("has\\backslash");
        // Server-supplied CSP keywords are dropped — keywords are owned by the
        // helper's hardcoded template, not by remote input.
        expect(header).not.toMatch(/script-src 'self' 'unsafe-inline' 'self'/);
    });

    it("canonicalizes URL entries to their origin (strips path, query, fragment)", () => {
        const header = buildMcpAppsCspHeader({
            connectDomains: ["https://api.example.com/some/path?x=1#frag"],
        });
        expect(header).toContain("connect-src 'self' https://api.example.com");
        expect(header).not.toContain("/some/path");
        expect(header).not.toContain("?x=1");
        expect(header).not.toContain("#frag");
    });

    it("accepts well-known bare-scheme sources (data:, blob:, mediastream:, filesystem:)", () => {
        const header = buildMcpAppsCspHeader({
            resourceDomains: ["data:", "blob:", "mediastream:", "filesystem:"],
        });
        expect(header).toContain(
            "script-src 'self' 'unsafe-inline' data: blob: mediastream: filesystem:"
        );
    });

    it("drops opaque-origin URLs that parse but have no real origin", () => {
        const header = buildMcpAppsCspHeader({
            resourceDomains: ["data:text/plain,injected", "javascript:alert(1)"],
        });
        // Opaque schemes are only allowed via the bare-scheme allowlist; the
        // data:-with-payload form parses but `URL.origin` is the literal
        // string "null", so it MUST be dropped.
        expect(header).not.toContain("data:text/plain");
        expect(header).not.toContain("javascript:");
        expect(header).not.toContain("alert(1)");
    });

    it("drops unparseable garbage entries", () => {
        const header = buildMcpAppsCspHeader({
            connectDomains: ["not-a-url", "://no-scheme", "https://valid.example"],
        });
        expect(header).toContain("connect-src 'self' https://valid.example");
        expect(header).not.toContain("not-a-url");
        expect(header).not.toContain("://no-scheme");
    });

    it("filters mixed valid/invalid entries, keeping only the safe ones", () => {
        const header = buildMcpAppsCspHeader({
            connectDomains: [
                "https://api.example.com",
                "evil.com; script-src *",
                "wss://realtime.example",
            ],
        });
        expect(header).toContain(
            "connect-src 'self' https://api.example.com wss://realtime.example"
        );
        expect(header).not.toContain("evil.com");
        expect(header).not.toContain("script-src *");
        // The directive boundary count MUST remain stable — no injected ';'.
        const directives = header.split(";").map((d) => d.trim());
        expect(directives).toContain(
            "connect-src 'self' https://api.example.com wss://realtime.example"
        );
    });

    it("treats a frameDomains list of only invalid entries as if it were empty (falls back to 'none')", () => {
        const header = buildMcpAppsCspHeader({
            frameDomains: ["evil; x", "also evil"],
        });
        expect(header).toContain("frame-src 'none'");
        expect(header).not.toContain("evil");
    });

    it("treats a baseUriDomains list of only invalid entries as if it were empty (falls back to 'self')", () => {
        const header = buildMcpAppsCspHeader({
            baseUriDomains: ["bad; injected"],
        });
        expect(header).toContain("base-uri 'self'");
        expect(header).not.toContain("injected");
    });
});

describe("buildMcpAppsAllowAttribute", () => {
    it("returns empty string when permissions is undefined", () => {
        expect(buildMcpAppsAllowAttribute(undefined)).toBe("");
    });

    it("returns empty string when no features are requested", () => {
        expect(buildMcpAppsAllowAttribute({})).toBe("");
    });

    it("maps each requested feature to its Permission Policy name", () => {
        expect(buildMcpAppsAllowAttribute({ camera: {} })).toBe("camera");
        expect(buildMcpAppsAllowAttribute({ microphone: {} })).toBe("microphone");
        expect(buildMcpAppsAllowAttribute({ geolocation: {} })).toBe("geolocation");
        // The hyphenated form per Permission Policy spec.
        expect(buildMcpAppsAllowAttribute({ clipboardWrite: {} })).toBe("clipboard-write");
    });

    it("joins multiple features with '; '", () => {
        const allow = buildMcpAppsAllowAttribute({
            camera: {},
            microphone: {},
            clipboardWrite: {},
        });
        expect(allow).toBe("camera; microphone; clipboard-write");
    });
});
