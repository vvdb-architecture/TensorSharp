#!/usr/bin/env python3
"""End-to-end sequential-vs-parallel benchmark for TensorSharp skill requests.

This drives the Web UI SSE API rather than the lower-level inference engine, so
each measurement includes every model round, skill lookup, network command, and
document writer used by the real requests.  It uses a fresh chat session for
every request and validates any downloadable artifacts advertised by SSE.

The benchmark intentionally uses only Python's standard library.  Start the
server separately, then run both phases (or select one with ``--phase``), for
example:

    python3 benchmarks/server_parallel_skills.py \
        --base-url http://127.0.0.1:5001 \
        --request-timeout 3600 --json-output /tmp/parallel-skills.json

Network-backed research is inherently variable.  Compare several runs and use
the per-prompt parallel/sequential ratios, not one absolute timing, when judging
an inference scheduling change.  ``TTFT`` is client-observed time to the first
non-queue SSE activity (answer text, reasoning, or built-in tool progress).  The
process exits nonzero if a request fails, lacks its requested PDF/PPTX, or
advertises an artifact that cannot be downloaded and structurally validated.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import dataclasses
import hashlib
import http.client
import io
import json
import socket
import sys
import threading
import time
import urllib.parse
import zipfile
from pathlib import Path
from typing import Any, Iterable


DEFAULT_PROMPTS = (
    (
        "stocks_pdf",
        "Search 10 stocks with most gains today and generate a pdf report to me.",
        ".pdf",
    ),
    (
        "apple_m6_pptx",
        "搜索apple M6的信息，并对比M5芯片，然后生成一份pptx给我。",
        ".pptx",
    ),
)


@dataclasses.dataclass
class ArtifactResult:
    name: str
    url: str
    expected_bytes: int = 0
    verified_by_server: bool = False
    http_status: int = 0
    content_type: str = ""
    downloaded_bytes: int = 0
    sha256: str = ""
    structure_ok: bool = False
    ok: bool = False
    error: str = ""


@dataclasses.dataclass
class RequestResult:
    phase: str
    prompt_id: str
    prompt: str
    expected_artifact_suffix: str
    session_id: str
    http_status: int = 0
    first_event_ms: float = 0.0
    ttft_ms: float = 0.0
    completion_ms: float = 0.0
    server_elapsed_s: float = 0.0
    server_tok_per_s: float = 0.0
    token_count: int = 0
    prompt_tokens: int = 0
    kv_reused_tokens: int = 0
    kv_reuse_percent: float = 0.0
    skill_steps: int = 0
    tool_execution_s: float = 0.0
    truncated: bool = False
    aborted: bool = False
    terminal_seen: bool = False
    answer_chars: int = 0
    error: str = ""
    artifacts: list[ArtifactResult] = dataclasses.field(default_factory=list)
    # Shared monotonic-clock coordinates are retained for aggregate parallel
    # timing and removed from the JSON report assembled below.
    _start_abs: float = 0.0
    _end_abs: float = 0.0

    @property
    def artifacts_ok(self) -> bool:
        suffix = self.expected_artifact_suffix.lower()
        matching = [a for a in self.artifacts if a.name.lower().endswith(suffix)]
        return bool(matching) and all(a.ok for a in matching)

    @property
    def ok(self) -> bool:
        return (
            not self.error
            and self.http_status == 200
            and self.terminal_seen
            and not self.aborted
            and self.artifacts_ok
        )


class HttpClient:
    def __init__(self, base_url: str, connect_timeout: float):
        parsed = urllib.parse.urlsplit(base_url.rstrip("/"))
        if parsed.scheme not in ("http", "https") or not parsed.hostname:
            raise ValueError("--base-url must be an absolute http:// or https:// URL")
        self.scheme = parsed.scheme
        self.host = parsed.hostname
        self.port = parsed.port or (443 if parsed.scheme == "https" else 80)
        self.base_path = parsed.path.rstrip("/")
        self.origin = f"{parsed.scheme}://{parsed.netloc}"
        self.connect_timeout = connect_timeout

    def _connect(self) -> http.client.HTTPConnection:
        cls = http.client.HTTPSConnection if self.scheme == "https" else http.client.HTTPConnection
        return cls(self.host, self.port, timeout=self.connect_timeout)

    def _path(self, path_or_url: str) -> str:
        parsed = urllib.parse.urlsplit(path_or_url)
        if parsed.scheme or parsed.netloc:
            return urllib.parse.urlunsplit(("", "", parsed.path or "/", parsed.query, ""))
        path = path_or_url if path_or_url.startswith("/") else "/" + path_or_url
        return self.base_path + path

    def absolute_url(self, path_or_url: str) -> str:
        return urllib.parse.urljoin(self.origin + self.base_path + "/", path_or_url)

    def json_request(
        self,
        method: str,
        path: str,
        payload: Any | None,
        timeout: float,
    ) -> tuple[int, Any]:
        body = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        headers = {"Accept": "application/json"}
        if body is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"
        conn = self._connect()
        try:
            conn.request(method, self._path(path), body=body, headers=headers)
            response = conn.getresponse()
            if conn.sock is not None:
                conn.sock.settimeout(timeout)
            raw = response.read()
            decoded = raw.decode("utf-8", errors="replace")
            try:
                value = json.loads(decoded) if decoded else None
            except json.JSONDecodeError:
                value = decoded
            return response.status, value
        finally:
            conn.close()

    def open_sse(
        self,
        path: str,
        payload: Any,
        request_timeout: float,
    ) -> tuple[http.client.HTTPConnection, http.client.HTTPResponse]:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        conn = self._connect()
        try:
            # ASP.NET does not commit the SSE response headers until the first
            # service frame is available.  Establish the socket with the short
            # connect timeout, then use the full request deadline while waiting
            # for that first frame/header as well as for subsequent frames.
            conn.connect()
            if conn.sock is not None:
                conn.sock.settimeout(request_timeout)
            conn.request(
                "POST",
                self._path(path),
                body=body,
                headers={
                    "Accept": "text/event-stream",
                    "Content-Type": "application/json; charset=utf-8",
                    "Content-Length": str(len(body)),
                },
            )
            response = conn.getresponse()
            return conn, response
        except Exception:
            conn.close()
            raise

    def download(
        self,
        path_or_url: str,
        timeout: float,
        max_bytes: int,
    ) -> tuple[int, str, bytes]:
        absolute = urllib.parse.urlsplit(self.absolute_url(path_or_url))
        expected_origin = urllib.parse.urlsplit(self.origin)
        if (absolute.scheme, absolute.netloc) != (expected_origin.scheme, expected_origin.netloc):
            raise ValueError("refusing to download an artifact from a different origin")

        conn = self._connect()
        try:
            path = urllib.parse.urlunsplit(("", "", absolute.path or "/", absolute.query, ""))
            conn.request("GET", path, headers={"Accept": "*/*"})
            response = conn.getresponse()
            if conn.sock is not None:
                conn.sock.settimeout(timeout)
            chunks: list[bytes] = []
            total = 0
            while True:
                chunk = response.read(min(1024 * 1024, max_bytes + 1 - total))
                if not chunk:
                    break
                chunks.append(chunk)
                total += len(chunk)
                if total > max_bytes:
                    raise ValueError(f"artifact exceeds validation limit of {max_bytes} bytes")
            return response.status, response.getheader("Content-Type", ""), b"".join(chunks)
        finally:
            conn.close()


def iter_sse(response: http.client.HTTPResponse, deadline: float) -> Iterable[dict[str, Any]]:
    data_lines: list[str] = []
    while True:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("request deadline expired while reading SSE")
        # Keep the socket deadline honest even when the server emits no heartbeat.
        try:
            if response.fp is not None and getattr(response.fp, "raw", None) is not None:
                sock = getattr(response.fp.raw, "_sock", None)
                if sock is not None:
                    sock.settimeout(remaining)
        except (AttributeError, OSError):
            pass

        raw = response.readline()
        if not raw:
            if data_lines:
                yield decode_sse_data(data_lines)
            return
        line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
        if line == "":
            if data_lines:
                yield decode_sse_data(data_lines)
                data_lines.clear()
            continue
        if line.startswith("data:"):
            data_lines.append(line[5:].lstrip())


def decode_sse_data(lines: list[str]) -> dict[str, Any]:
    payload = "\n".join(lines)
    if payload == "[DONE]":
        return {"_done_sentinel": True}
    try:
        value = json.loads(payload)
    except json.JSONDecodeError as ex:
        return {"_malformed_sse": payload, "_parse_error": str(ex)}
    return value if isinstance(value, dict) else {"_sse_value": value}


def create_session(client: HttpClient, timeout: float) -> str:
    status, payload = client.json_request("POST", "/api/sessions", None, timeout)
    if status != 200 or not isinstance(payload, dict) or not payload.get("sessionId"):
        raise RuntimeError(f"could not create a chat session: HTTP {status}: {payload!r}")
    return str(payload["sessionId"])


def delete_session(client: HttpClient, session_id: str, timeout: float) -> str:
    try:
        status, payload = client.json_request("DELETE", f"/api/sessions/{session_id}", None, timeout)
        if status != 200:
            return f"session cleanup returned HTTP {status}: {payload!r}"
    except Exception as ex:  # cleanup must not hide the benchmark result
        return f"session cleanup failed: {ex}"
    return ""


def is_model_activity(event: dict[str, Any]) -> bool:
    return any(event.get(key) not in (None, "", [], {}) for key in ("token", "thinking", "replace")) or (
        event.get("tool_progress") in ("writing", "running", "finished")
    ) or bool(event.get("skill_step"))


def collect_artifacts(
    artifacts: dict[str, ArtifactResult],
    event: dict[str, Any],
) -> None:
    files = event.get("files")
    if not isinstance(files, list):
        return
    server_verified = event.get("artifact_verified") is True
    for item in files:
        if not isinstance(item, dict) or not item.get("url"):
            continue
        url = str(item["url"])
        existing = artifacts.get(url)
        if existing is None:
            raw_bytes = item.get("bytes", 0)
            try:
                expected_bytes = int(raw_bytes or 0)
            except (TypeError, ValueError):
                expected_bytes = 0
            existing = ArtifactResult(
                name=str(item.get("name") or Path(urllib.parse.urlsplit(url).path).name),
                url=url,
                expected_bytes=expected_bytes,
            )
            artifacts[url] = existing
        existing.verified_by_server = existing.verified_by_server or server_verified


def run_request(
    client: HttpClient,
    phase: str,
    prompt_spec: tuple[str, str, str],
    session_id: str,
    max_tokens: int,
    temperature: float,
    think: bool,
    skills: list[str] | None,
    discovery: bool,
    request_timeout: float,
    start_gate: threading.Barrier | None = None,
) -> RequestResult:
    prompt_id, prompt, expected_suffix = prompt_spec
    result = RequestResult(phase, prompt_id, prompt, expected_suffix, session_id)
    artifacts: dict[str, ArtifactResult] = {}
    answer_chars = 0
    payload: dict[str, Any] = {
        "messages": [{"role": "user", "content": prompt}],
        "sessionId": session_id,
        "newChat": False,
        "maxTokens": max_tokens,
        "temperature": temperature,
        "think": think,
        "tools": [],
        "skills_discovery": discovery,
    }
    if skills is not None:
        payload["skills"] = skills

    conn: http.client.HTTPConnection | None = None
    try:
        if start_gate is not None:
            start_gate.wait(timeout=max(30.0, client.connect_timeout * 2))
        result._start_abs = time.monotonic()
        deadline = result._start_abs + request_timeout
        conn, response = client.open_sse("/api/chat", payload, request_timeout)
        result.http_status = response.status
        if response.status != 200:
            raw = response.read().decode("utf-8", errors="replace")
            result.error = f"HTTP {response.status}: {raw[:2000]}"
            return result

        for event in iter_sse(response, deadline):
            now = time.monotonic()
            elapsed_ms = (now - result._start_abs) * 1000.0
            if result.first_event_ms == 0.0:
                result.first_event_ms = elapsed_ms
            if result.ttft_ms == 0.0 and is_model_activity(event):
                result.ttft_ms = elapsed_ms

            collect_artifacts(artifacts, event)
            if event.get("skill_step"):
                result.skill_steps += 1
            if event.get("tool_progress") == "finished":
                try:
                    result.tool_execution_s += float(event.get("seconds", 0.0) or 0.0)
                except (TypeError, ValueError):
                    pass
            for key in ("token", "thinking", "replace"):
                value = event.get(key)
                if isinstance(value, str):
                    answer_chars += len(value)

            if event.get("done") is True:
                result.terminal_seen = True
                result.server_elapsed_s = float(event.get("elapsed", 0.0) or 0.0)
                result.server_tok_per_s = float(event.get("tokPerSec", 0.0) or 0.0)
                result.token_count = int(event.get("tokenCount", 0) or 0)
                result.prompt_tokens = int(event.get("promptTokens", 0) or 0)
                result.kv_reused_tokens = int(event.get("kvReusedTokens", 0) or 0)
                result.kv_reuse_percent = float(event.get("kvReusePercent", 0.0) or 0.0)
                result.truncated = bool(event.get("truncated", False))
                result.aborted = bool(event.get("aborted", False))
                if event.get("error"):
                    result.error = str(event["error"])
                break
    except (TimeoutError, socket.timeout) as ex:
        result.error = f"timed out after {request_timeout:g}s: {ex}"
    except Exception as ex:
        result.error = f"{type(ex).__name__}: {ex}"
    finally:
        result._end_abs = time.monotonic()
        result.completion_ms = (result._end_abs - result._start_abs) * 1000.0 if result._start_abs else 0.0
        result.answer_chars = answer_chars
        result.artifacts = list(artifacts.values())
        if conn is not None:
            conn.close()
    return result


def validate_structure(name: str, data: bytes) -> tuple[bool, str]:
    lower = name.lower()
    if lower.endswith(".pdf"):
        if not data.startswith(b"%PDF-"):
            return False, "missing PDF header"
        if b"%%EOF" not in data[-4096:]:
            return False, "missing PDF EOF marker"
        return True, ""
    if lower.endswith((".pptx", ".docx", ".xlsx")):
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                names = set(archive.namelist())
                required = {"[Content_Types].xml"}
                if lower.endswith(".pptx"):
                    required.add("ppt/presentation.xml")
                elif lower.endswith(".docx"):
                    required.add("word/document.xml")
                else:
                    required.add("xl/workbook.xml")
                missing = sorted(required - names)
                bad_member = archive.testzip()
                if missing:
                    return False, "missing package entries: " + ", ".join(missing)
                if bad_member:
                    return False, f"corrupt ZIP member: {bad_member}"
        except (OSError, zipfile.BadZipFile) as ex:
            return False, f"invalid Office ZIP package: {ex}"
        return True, ""
    return bool(data), "artifact is empty" if not data else ""


def validate_artifacts(
    client: HttpClient,
    requests: list[RequestResult],
    timeout: float,
    max_bytes: int,
) -> None:
    cache: dict[str, ArtifactResult] = {}
    for result in requests:
        for artifact in result.artifacts:
            if artifact.url in cache:
                prior = cache[artifact.url]
                artifact.http_status = prior.http_status
                artifact.content_type = prior.content_type
                artifact.downloaded_bytes = prior.downloaded_bytes
                artifact.sha256 = prior.sha256
                artifact.structure_ok = prior.structure_ok
                artifact.ok = prior.ok
                artifact.error = prior.error
                continue
            try:
                status, content_type, data = client.download(artifact.url, timeout, max_bytes)
                artifact.http_status = status
                artifact.content_type = content_type
                artifact.downloaded_bytes = len(data)
                artifact.sha256 = hashlib.sha256(data).hexdigest()
                structure_ok, structure_error = validate_structure(artifact.name, data)
                artifact.structure_ok = structure_ok
                problems = []
                if status != 200:
                    problems.append(f"download returned HTTP {status}")
                if artifact.expected_bytes and artifact.expected_bytes != len(data):
                    problems.append(
                        f"SSE declared {artifact.expected_bytes} bytes but downloaded {len(data)}"
                    )
                if not structure_ok:
                    problems.append(structure_error)
                artifact.error = "; ".join(p for p in problems if p)
                artifact.ok = not artifact.error
            except Exception as ex:
                artifact.error = f"{type(ex).__name__}: {ex}"
                artifact.ok = False
            cache[artifact.url] = dataclasses.replace(artifact)


def run_phase(
    client: HttpClient,
    phase: str,
    prompt_specs: tuple[tuple[str, str, str], ...],
    args: argparse.Namespace,
) -> tuple[list[RequestResult], list[str]]:
    sessions: list[str] = []
    cleanup_warnings: list[str] = []
    skills = (
        None
        if args.skills is None
        else [item.strip() for item in args.skills.split(",") if item.strip()]
    )
    common = dict(
        client=client,
        phase=phase,
        max_tokens=args.max_tokens,
        temperature=args.temperature,
        think=args.think,
        skills=skills,
        discovery=not args.no_discovery,
        request_timeout=args.request_timeout,
    )
    try:
        # Build the list under the cleanup guard: if the third session creation
        # fails, the first two must not be leaked on the server.
        sessions.extend(create_session(client, args.connect_timeout) for _ in prompt_specs)
        if phase == "sequential":
            results = [
                run_request(prompt_spec=spec, session_id=session_id, **common)
                for spec, session_id in zip(prompt_specs, sessions)
            ]
        else:
            gate = threading.Barrier(len(prompt_specs) + 1)
            with concurrent.futures.ThreadPoolExecutor(max_workers=len(prompt_specs)) as pool:
                futures = [
                    pool.submit(
                        run_request,
                        prompt_spec=spec,
                        session_id=session_id,
                        start_gate=gate,
                        **common,
                    )
                    for spec, session_id in zip(prompt_specs, sessions)
                ]
                gate.wait(timeout=max(30.0, args.connect_timeout * 2))
                results = [future.result() for future in futures]
        # Artifact URLs may be backed by the session workspace. Validate while
        # these fresh sessions still exist, then always clean them up below.
        validate_artifacts(
            client,
            results,
            args.artifact_timeout,
            args.artifact_max_mb * 1024 * 1024,
        )
    finally:
        for session_id in sessions:
            warning = delete_session(client, session_id, args.connect_timeout)
            if warning:
                cleanup_warnings.append(warning)
    return results, cleanup_warnings


def phase_wall_ms(results: list[RequestResult], phase: str) -> float:
    if not results:
        return 0.0
    if phase == "sequential":
        return sum(r.completion_ms for r in results)
    starts = [r._start_abs for r in results if r._start_abs]
    ends = [r._end_abs for r in results if r._end_abs]
    return (max(ends) - min(starts)) * 1000.0 if starts and ends else 0.0


def serializable_request(result: RequestResult) -> dict[str, Any]:
    value = dataclasses.asdict(result)
    value.pop("_start_abs", None)
    value.pop("_end_abs", None)
    value["artifacts_ok"] = result.artifacts_ok
    value["ok"] = result.ok
    return value


def print_results(phases: dict[str, list[RequestResult]]) -> None:
    print()
    print(
        f"{'phase':<11} {'request':<16} {'TTFT ms':>10} {'complete ms':>12} "
        f"{'tokens':>7} {'prompt':>7} {'tool s':>8} {'files':>6} {'status':>8}"
    )
    print("-" * 96)
    for phase, results in phases.items():
        for result in results:
            print(
                f"{phase:<11} {result.prompt_id:<16} {result.ttft_ms:>10.1f} "
                f"{result.completion_ms:>12.1f} {result.token_count:>7} "
                f"{result.prompt_tokens:>7} {result.tool_execution_s:>8.1f} "
                f"{len(result.artifacts):>6} {('ok' if result.ok else 'FAIL'):>8}"
            )
            if result.error:
                print(f"  error: {result.error}")
            if not result.artifacts:
                print(f"  artifact: no {result.expected_artifact_suffix} path was advertised by SSE")
            for artifact in result.artifacts:
                state = "ok" if artifact.ok else "FAIL"
                verified = " server-verified" if artifact.verified_by_server else ""
                print(
                    f"  artifact [{state}{verified}] {artifact.name}: "
                    f"{artifact.downloaded_bytes} bytes {artifact.url}"
                    + (f" ({artifact.error})" if artifact.error else "")
                )

    sequential = phases.get("sequential", [])
    parallel = phases.get("parallel", [])
    if sequential and parallel:
        seq_wall = phase_wall_ms(sequential, "sequential")
        par_wall = phase_wall_ms(parallel, "parallel")
        print()
        print(f"sequential aggregate request wall: {seq_wall / 1000.0:.2f}s")
        print(f"parallel aggregate wall window:   {par_wall / 1000.0:.2f}s")
        if par_wall > 0:
            print(f"parallel workload speedup:        {seq_wall / par_wall:.2f}x")
        seq_by_id = {r.prompt_id: r for r in sequential}
        for par in parallel:
            seq = seq_by_id.get(par.prompt_id)
            if seq and seq.completion_ms > 0:
                print(
                    f"{par.prompt_id} parallel completion slowdown: "
                    f"{par.completion_ms / seq.completion_ms:.2f}x"
                )
    elif sequential:
        print()
        print(
            "sequential aggregate request wall: "
            f"{phase_wall_ms(sequential, 'sequential') / 1000.0:.2f}s"
        )
    elif parallel:
        print()
        print(
            "parallel aggregate wall window:   "
            f"{phase_wall_ms(parallel, 'parallel') / 1000.0:.2f}s"
        )


def preflight(client: HttpClient, timeout: float) -> dict[str, Any]:
    health_status, health = client.json_request("GET", "/health", None, timeout)
    models_status, models = client.json_request("GET", "/api/models", None, timeout)
    skills_status, skills = client.json_request("GET", "/api/skills", None, timeout)
    if health_status != 200 or models_status != 200:
        raise RuntimeError(
            f"server preflight failed: health HTTP {health_status}, models HTTP {models_status}"
        )
    skill_names: list[str] = []
    if isinstance(skills, dict):
        for item in skills.get("skills", []) or []:
            if isinstance(item, dict) and (item.get("id") or item.get("name")):
                skill_names.append(str(item.get("id") or item.get("name")))
    return {
        "health_status": health_status,
        "health": health,
        "models_status": models_status,
        "models": models,
        "skills_status": skills_status,
        "skill_names": skill_names,
    }


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:5001")
    parser.add_argument("--connect-timeout", type=float, default=30.0)
    parser.add_argument("--request-timeout", type=float, default=3600.0)
    parser.add_argument("--artifact-timeout", type=float, default=60.0)
    parser.add_argument("--artifact-max-mb", type=int, default=256)
    parser.add_argument("--max-tokens", type=int, default=20000)
    parser.add_argument("--temperature", type=float, default=0.0)
    parser.add_argument("--think", action="store_true")
    parser.add_argument(
        "--skills",
        help="comma-separated explicit skill names; default leaves selection to discovery",
    )
    parser.add_argument("--no-discovery", action="store_true")
    parser.add_argument(
        "--phase-order",
        choices=("sequential-first", "parallel-first"),
        default="sequential-first",
        help="order to use when --phase=both",
    )
    parser.add_argument(
        "--phase",
        choices=("both", "sequential", "parallel"),
        default="both",
        help="run both phases (default) or one phase for a focused A/B rerun",
    )
    parser.add_argument("--json-output", type=Path)
    args = parser.parse_args(argv)
    if args.connect_timeout <= 0 or args.request_timeout <= 0 or args.artifact_timeout <= 0:
        parser.error("timeouts must be positive")
    if args.artifact_max_mb <= 0 or args.max_tokens <= 0:
        parser.error("--artifact-max-mb and --max-tokens must be positive")
    return args


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    client = HttpClient(args.base_url, args.connect_timeout)
    try:
        server = preflight(client, args.connect_timeout)
    except Exception as ex:
        print(f"preflight failed: {ex}", file=sys.stderr)
        return 2

    print(f"server: {args.base_url}")
    print(f"skills ({len(server['skill_names'])}): {', '.join(server['skill_names']) or '(none)'}")
    print(f"max tokens/request: {args.max_tokens}; request timeout: {args.request_timeout:g}s")

    if args.phase == "both":
        order = (
            ("sequential", "parallel")
            if args.phase_order == "sequential-first"
            else ("parallel", "sequential")
        )
    else:
        order = (args.phase,)
    phases: dict[str, list[RequestResult]] = {}
    warnings: list[str] = []
    for phase in order:
        print(f"running {phase} phase...", flush=True)
        try:
            results, cleanup = run_phase(client, phase, DEFAULT_PROMPTS, args)
            phases[phase] = results
            warnings.extend(cleanup)
        except Exception as ex:
            print(f"{phase} phase failed before completion: {ex}", file=sys.stderr)
            return 2

    all_requests = [result for phase in phases.values() for result in phase]
    print_results(phases)
    for warning in warnings:
        print(f"warning: {warning}", file=sys.stderr)

    sequential = phases.get("sequential", [])
    parallel = phases.get("parallel", [])
    sequential_wall_ms = phase_wall_ms(sequential, "sequential") if sequential else None
    parallel_wall_ms = phase_wall_ms(parallel, "parallel") if parallel else None
    expected_request_count = len(DEFAULT_PROMPTS) * len(order)
    all_requests_ok = (
        len(all_requests) == expected_request_count
        and all(result.ok for result in all_requests)
    )
    report = {
        "base_url": args.base_url,
        "phase": args.phase,
        "phase_order": list(order),
        "max_tokens": args.max_tokens,
        "temperature": args.temperature,
        "think": args.think,
        "skills": (
            None
            if args.skills is None
            else [item.strip() for item in args.skills.split(",") if item.strip()]
        ),
        "skills_discovery": not args.no_discovery,
        "server": server,
        "phases": {
            name: [serializable_request(result) for result in results]
            for name, results in phases.items()
        },
        "summary": {
            "sequential_wall_ms": sequential_wall_ms,
            "parallel_wall_ms": parallel_wall_ms,
            "parallel_speedup": (
                sequential_wall_ms / parallel_wall_ms
                if sequential_wall_ms is not None
                and parallel_wall_ms is not None
                and parallel_wall_ms > 0
                else None
            ),
            "completed_request_count": len(all_requests),
            "expected_request_count": expected_request_count,
            "all_requests_ok": all_requests_ok,
        },
        "cleanup_warnings": warnings,
    }
    if args.json_output:
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(
            json.dumps(report, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"JSON report: {args.json_output}")

    return 0 if report["summary"]["all_requests_ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
