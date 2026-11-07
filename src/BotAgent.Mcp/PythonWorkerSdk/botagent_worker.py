"""BotAgent protocol-v1 SDK. Python >=3.11, standard library only.

Package code is trusted local code, NOT sandboxed. Context capabilities are host
metadata; they never create authority to bypass the host tool approval gate.
"""
import contextlib
import inspect
import json
import sys
from dataclasses import dataclass
from types import MappingProxyType
from typing import Any, Callable, Mapping

PROTOCOL_VERSION = 1
MAX_FRAME_BYTES = 512 * 1024
MAX_TOOLS = 64


@dataclass(frozen=True)
class Context:
    configuration: Mapping[str, Any]
    capabilities: tuple[str, ...]

    def __repr__(self) -> str:
        return "Context(configuration=<redacted>, capabilities=<metadata>)"

    def require_capability(self, capability: str) -> None:
        """Fail closed for SDK code; this is not OS permission enforcement."""
        if capability not in self.capabilities:
            raise PermissionError("capability_not_declared")


class Plugin:
    """Register tools and lifecycle hooks, then serve the host's stdio channel."""

    def __init__(self) -> None:
        self._tools: dict[str, tuple[dict, Callable]] = {}
        self._load: Callable | None = None
        self._unload: Callable | None = None
        self._context = Context(MappingProxyType({}), ())
        self._initialized = False

    def tool(self, *, name: str, description: str, input_schema: dict,
             read_only: bool = False) -> Callable:
        if (not name or len(name) > 64 or not name.isascii()
                or any(not (c.isalnum() or c in "_-") for c in name)):
            raise ValueError("invalid_tool_name")
        if (not isinstance(input_schema, dict) or input_schema.get("type") != "object"
                or len(description) > 400):
            raise ValueError("invalid_tool_metadata")
        # Round-trip at registration: reject unserializable schema, detach author mutations.
        schema = json.loads(json.dumps(input_schema, allow_nan=False))
        if len(json.dumps(schema).encode("utf-8")) > 16 * 1024:
            raise ValueError("schema_too_large")

        def register(function: Callable) -> Callable:
            if name in self._tools or len(self._tools) >= MAX_TOOLS:
                raise ValueError("duplicate_or_excess_tools")
            self._tools[name] = ({"name": name, "description": description,
                                  "inputSchema": schema,
                                  "annotations": {"readOnlyHint": bool(read_only)}}, function)
            return function
        return register

    def on_load(self, function: Callable) -> Callable:
        if self._load is not None:
            raise ValueError("duplicate_load_hook")
        self._load = function
        return function

    def on_unload(self, function: Callable) -> Callable:
        if self._unload is not None:
            raise ValueError("duplicate_unload_hook")
        self._unload = function
        return function

    @staticmethod
    def _invoke(function: Callable, *arguments: Any) -> Any:
        value = function(*arguments)
        if inspect.isawaitable(value):
            import asyncio
            return asyncio.run(value)
        return value

    def _dispatch(self, method: str, parameters: dict) -> Any:
        if method == "initialize":
            if self._initialized or parameters.get("protocolVersion") != PROTOCOL_VERSION:
                raise ValueError("invalid_initialize")
            config = parameters.get("configuration", {})
            capabilities = parameters.get("capabilities", [])
            if (not isinstance(config, dict) or not isinstance(capabilities, list)
                    or any(not isinstance(c, str) for c in capabilities)):
                raise ValueError("invalid_initialize")
            self._context = Context(MappingProxyType(config), tuple(capabilities))
            if self._load is not None:
                self._invoke(self._load, self._context)
            self._initialized = True
            return {"protocolVersion": PROTOCOL_VERSION}
        if not self._initialized:
            raise ValueError("not_initialized")
        if method == "tools/list":
            return {"tools": [metadata for metadata, _ in self._tools.values()]}
        if method == "tools/call":
            name, arguments = parameters.get("name"), parameters.get("arguments")
            if name not in self._tools or not isinstance(arguments, dict):
                raise ValueError("invalid_tool_call")
            try:
                result = self._invoke(self._tools[name][1], arguments, self._context)
                content = result if isinstance(result, str) else json.dumps(result, ensure_ascii=False, allow_nan=False)
                return {"content": content, "isError": False}
            except Exception:
                # Exceptions can contain secrets, arguments or local file names: never serialize them.
                return {"content": "", "isError": True}
        if method == "shutdown":
            try:
                if self._unload is not None:
                    self._invoke(self._unload, self._context)
            finally:
                self._initialized = False
            return {}
        raise ValueError("unknown_method")

    def run(self) -> None:
        if sys.version_info < (3, 11) or sys.version_info.major != 3:
            raise RuntimeError("unsupported_python_version")
        protocol_output = sys.stdout
        protocol_input = sys.stdin.buffer
        while True:
            raw = protocol_input.readline(MAX_FRAME_BYTES + 1)
            if not raw:
                return
            if len(raw) > MAX_FRAME_BYTES or not raw.endswith(b"\n"):
                return  # framing lost: close channel, never parse a partial/oversized message
            request_id = None
            method = None
            try:
                request = json.loads(raw)
                if (not isinstance(request, dict) or request.get("jsonrpc") != "2.0"
                        or type(request.get("id")) is not int
                        or not isinstance(request.get("method"), str)
                        or not isinstance(request.get("params"), dict)):
                    raise ValueError("invalid_request")
                request_id, method = request["id"], request["method"]
                # Ordinary author print() belongs to diagnostics, not protocol stdout.
                with contextlib.redirect_stdout(sys.stderr):
                    result = self._dispatch(method, request["params"])
                response = {"jsonrpc": "2.0", "id": request_id, "result": result}
            except Exception:
                response = {"jsonrpc": "2.0", "id": request_id,
                            "error": {"code": -32603, "message": "worker_request_failed"}}
            encoded = json.dumps(response, ensure_ascii=False, allow_nan=False, separators=(",", ":"))
            if len(encoded.encode("utf-8")) > MAX_FRAME_BYTES:
                encoded = json.dumps({"jsonrpc": "2.0", "id": request_id,
                                      "error": {"code": -32001, "message": "result_too_large"}})
            protocol_output.write(encoded + "\n")
            protocol_output.flush()
            if method == "shutdown":
                return
