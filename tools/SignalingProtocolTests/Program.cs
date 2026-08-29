// Standalone TDD harness for WebRtcSignalingProtocol (pure string logic of the
// teleimager HTTP signaling client). Run: dotnet run --project tools/SignalingProtocolTests
// Exit code 0 = all green. No NUnit: Unity's test framework package is not
// installed in this fork, so tests live outside Assets and compile the subject
// file directly.

using System;
using System.Text.Json;
using PicoBridge.Camera;

const string SdpNoCandidates =
    "v=0\r\n" +
    "o=- 46117317 2 IN IP4 127.0.0.1\r\n" +
    "s=-\r\n" +
    "t=0 0\r\n" +
    "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
    "a=mid:0\r\n" +
    "a=recvonly\r\n" +
    "a=fingerprint:sha-256 AB:CD\r\n";

const string SdpWithCandidates = SdpNoCandidates +
    "a=candidate:1 1 UDP 2122187007 192.168.1.10 52312 typ host\r\n";

const string SdpTwoMediaSections =
    "v=0\r\n" +
    "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n" +
    "a=mid:0\r\n" +
    "a=recvonly\r\n" +
    "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
    "a=mid:1\r\n";

var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok)
        Console.WriteLine($"  PASS  {name}");
    else
    {
        failures++;
        Console.WriteLine($"! FAIL  {name}{(detail.Length > 0 ? "  -- " + detail : "")}");
    }
}

// --- PatchSdpWithCandidates ---

// 1. SDP already carries candidates -> untouched (libwebrtc merged them).
Check("patch: already has candidates -> unchanged",
    ReferenceEquals(WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpWithCandidates,
        new[] { "candidate:9 1 UDP 1 10.0.0.1 5000 typ host" }), SdpWithCandidates));

// 2. Missing candidates -> appended at end with end-of-candidates marker.
var patched = WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpNoCandidates,
    new[] { "candidate:1 1 UDP 2122187007 192.168.1.10 52312 typ host",
            "candidate:2 1 UDP 1686052607 10.0.0.5 40000 typ host" });
Check("patch: appends candidates then end-of-candidates",
    patched.EndsWith("a=fingerprint:sha-256 AB:CD\r\n" +
        "a=candidate:1 1 UDP 2122187007 192.168.1.10 52312 typ host\r\n" +
        "a=candidate:2 1 UDP 1686052607 10.0.0.5 40000 typ host\r\n" +
        "a=end-of-candidates\r\n"),
    $"got tail: {patched.Substring(Math.Max(0, patched.Length - 220))}");
Check("patch: keeps original content",
    patched.StartsWith("v=0\r\n") && patched.Contains("a=fingerprint:sha-256 AB:CD\r\n"));

// 3. Two media sections -> insert before the second m= line, not at the end.
var patchedTwo = WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpTwoMediaSections,
    new[] { "candidate:1 1 UDP 1 192.168.1.10 52312 typ host" });
Check("patch: inserts before second media section",
    patchedTwo.Contains("a=candidate:1 1 UDP 1 192.168.1.10 52312 typ host\r\n" +
        "a=end-of-candidates\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n"),
    patchedTwo);

// 4. Candidate list without usable entries -> unchanged.
Check("patch: empty list -> unchanged",
    WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpNoCandidates, new string[0]) == SdpNoCandidates);
Check("patch: null list -> unchanged",
    WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpNoCandidates, null) == SdpNoCandidates);
Check("patch: blank candidates filtered -> unchanged",
    WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpNoCandidates, new[] { "", "  ", null }) == SdpNoCandidates);

// 5. Tolerates pre-escaped "a=" prefix on candidate payloads.
var patchedPrefix = WebRtcSignalingProtocol.PatchSdpWithCandidates(SdpNoCandidates,
    new[] { "a=candidate:1 1 UDP 1 192.168.1.10 52312 typ host" });
Check("patch: strips duplicate a= prefix",
    patchedPrefix.Contains("\r\na=candidate:1 1 UDP 1 192.168.1.10 52312 typ host\r\n") &&
    !patchedPrefix.Contains("a=a=candidate"));

// 6. LF-only SDP -> LF line endings in the patch block.
var sdpLf = SdpNoCandidates.Replace("\r\n", "\n");
var patchedLf = WebRtcSignalingProtocol.PatchSdpWithCandidates(sdpLf,
    new[] { "candidate:1 1 UDP 1 192.168.1.10 52312 typ host" });
Check("patch: LF-only sdp uses LF",
    patchedLf.Contains("\na=candidate:1 1 UDP 1 192.168.1.10 52312 typ host\n") &&
    !patchedLf.Contains("\r\n"));

// 7. SDP without trailing line break -> separator inserted.
var sdpNoTrail = SdpNoCandidates.TrimEnd('\r', '\n');
var patchedNoTrail = WebRtcSignalingProtocol.PatchSdpWithCandidates(sdpNoTrail,
    new[] { "candidate:1 1 UDP 1 192.168.1.10 52312 typ host" });
Check("patch: adds separator when sdp lacks trailing break",
    patchedNoTrail.Contains("a=fingerprint:sha-256 AB:CD\r\na=candidate:1 1 UDP 1 192.168.1.10 52312 typ host\r\n"));

// 8. Null / empty sdp passthrough.
Check("patch: null sdp -> null", WebRtcSignalingProtocol.PatchSdpWithCandidates(null,
    new[] { "candidate:1 1 UDP 1 192.168.1.10 52312 typ host" }) == null);
Check("patch: empty sdp -> empty", WebRtcSignalingProtocol.PatchSdpWithCandidates("",
    new[] { "candidate:1 1 UDP 1 192.168.1.10 52312 typ host" }) == "");

// --- BuildOfferRequestBody ---

// 9. Round-trip: server side parses with a standard JSON parser.
var body = WebRtcSignalingProtocol.BuildOfferRequestBody(SdpWithCandidates, "h264");
using (var doc = JsonDocument.Parse(body))
{
    var root = doc.RootElement;
    Check("body: sdp round-trips",
        root.GetProperty("sdp").GetString() == SdpWithCandidates);
    Check("body: type is offer", root.GetProperty("type").GetString() == "offer");
    Check("body: codec field", root.GetProperty("codec").GetString() == "h264");
}

// 10. codec=null -> JSON null (server falls back to its config).
var bodyNullCodec = WebRtcSignalingProtocol.BuildOfferRequestBody(SdpWithCandidates, null);
using (var docNull = JsonDocument.Parse(bodyNullCodec))
    Check("body: null codec serializes as JSON null",
        docNull.RootElement.GetProperty("codec").ValueKind == JsonValueKind.Null);

// 11. Quotes and backslashes inside SDP survive the round-trip.
const string sdpNasty = "v=0\r\na=foo\"bar\\baz\r\n";
var bodyNasty = WebRtcSignalingProtocol.BuildOfferRequestBody(sdpNasty, "h264");
using (var docNasty = JsonDocument.Parse(bodyNasty))
    Check("body: escapes quotes and backslashes",
        docNasty.RootElement.GetProperty("sdp").GetString() == sdpNasty, bodyNasty);

// --- ExtractJsonString ---

// 12. Answer-shaped payload with escaped CRLF.
Check("extract: sdp with escaped crlf",
    WebRtcSignalingProtocol.ExtractJsonString("{\"sdp\":\"v=0\\r\\na=x\",\"type\":\"answer\"}", "sdp") == "v=0\r\na=x");
Check("extract: type field",
    WebRtcSignalingProtocol.ExtractJsonString("{\"sdp\":\"v=0\",\"type\":\"answer\"}", "type") == "answer");

// 13. Missing key / escaped quote.
Check("extract: missing key -> empty",
    WebRtcSignalingProtocol.ExtractJsonString("{\"type\":\"answer\"}", "sdp") == "");
Check("extract: escaped quote inside value",
    WebRtcSignalingProtocol.ExtractJsonString("{\"sdp\":\"a=\\\"q\\\"\"}", "sdp") == "a=\"q\"");

Console.WriteLine(failures == 0 ? "\nALL GREEN" : $"\n{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
