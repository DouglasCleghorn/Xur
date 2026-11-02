"""Xur MCP stdio server, installed in T3's persistent home. Standard library only."""
import base64
import http.client
import json
from pathlib import Path
import ssl
import sys
import urllib.parse

LIMIT = 8 * 1024 * 1024
VERSIONS = ("2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05")


def request(method, path, body=None):
    parsed = urllib.parse.urlsplit(path)
    if (not path.startswith("/api/") or parsed.scheme or parsed.netloc or parsed.fragment
            or any(c in parsed.path for c in ("%", "\\")) or ".." in parsed.path or "//" in parsed.path
            or any(ord(c) < 32 for c in path)):
        raise ValueError("Use a local /api/ path from xur_catalog.")
    config = json.loads((Path(__file__).parent / "connection.json").read_text())
    origin = urllib.parse.urlsplit(config["origin"])
    if origin.scheme != "https" or origin.username or origin.path not in ("", "/") or origin.query or origin.fragment:
        raise ValueError("Reconnect Xur MCP using Xur's HTTPS address.")
    context = ssl.create_default_context(cadata=config["certificate"])
    connection = http.client.HTTPSConnection(origin.hostname, origin.port or 443, context=context, timeout=120)
    try:
        connection.request(method, path, body=None if body is None else json.dumps(body).encode(),
                           headers={"Authorization": "Bearer " + config["apiKey"], "Content-Type": "application/json"})
        response = connection.getresponse()
        data = response.read(LIMIT + 1)
        if len(data) > LIMIT:
            raise ValueError("Response exceeds 8 MiB. Narrow the query or use the download API directly.")
        media = response.getheader("Content-Type", "")
        binary = not ("json" in media or media.startswith("text/") or not data)
        return dict(status=response.status, contentType=media,
                    encoding="base64" if binary else "utf-8",
                    body=base64.b64encode(data).decode() if binary else data.decode("utf-8"))
    finally:
        connection.close()


def tool(name, description, properties, required, read):
    return dict(name=name, description=description,
                inputSchema=dict(type="object", properties=properties, required=required, additionalProperties=False),
                annotations=dict(readOnlyHint=read, destructiveHint=not read, openWorldHint=False))


TOOLS = [
    tool("xur_catalog", "Discover Xur API data, settings, route parameters and request schemas. Call first.", {}, [], True),
    tool("xur_read", "Read Xur data or settings using a catalog API path, with optional query string.",
         {"path": {"type": "string"}}, ["path"], True),
    tool("xur_write", "Change Xur settings or perform an action. Read current values first; preserve revision and approval fields. Requires Change Xur settings permission.",
         {"path": {"type": "string"}, "method": {"type": "string", "enum": ["POST", "PUT", "PATCH", "DELETE"]}, "body": {}}, ["path", "method"], False),
]


def dispatch(message):
    if not isinstance(message, dict) or message.get("jsonrpc") != "2.0" or not isinstance(message.get("method"), str):
        return dict(jsonrpc="2.0", id=None, error=dict(code=-32600, message="Invalid request"))
    if "id" not in message:
        return None
    identifier, method = message["id"], message["method"]
    def reply(result):
        return dict(jsonrpc="2.0", id=identifier, result=result)
    def error(code, text):
        return dict(jsonrpc="2.0", id=identifier, error=dict(code=code, message=text))
    params = message.get("params", {})
    if not isinstance(params, dict):
        return error(-32602, "Expected an object for params")
    if method == "initialize":
        version = params.get("protocolVersion")
        return reply(dict(protocolVersion=version if version in VERSIONS else VERSIONS[0],
                          capabilities=dict(tools={}), serverInfo=dict(name="xur", version="1.0")))
    if method == "ping":
        return reply({})
    if method == "tools/list":
        return reply(dict(tools=TOOLS))
    if method != "tools/call":
        return error(-32601, "Method not found")
    name, args = params.get("name"), params.get("arguments", {})
    definition = next((t for t in TOOLS if t["name"] == name), None)
    if definition is None:
        return error(-32602, "Unknown tool")
    schema = definition["inputSchema"]
    if not isinstance(args, dict) or not set(schema["required"]) <= args.keys() or args.keys() - schema["properties"].keys():
        return error(-32602, "Invalid tool arguments")
    if name != "xur_catalog" and not isinstance(args["path"], str):
        return error(-32602, "Expected a string path")
    if name == "xur_write" and args["method"] not in ("POST", "PUT", "PATCH", "DELETE"):
        return error(-32602, "Unsupported write method")
    try:
        result = request("GET" if name != "xur_write" else args["method"],
                         "/api/t3-code/mcp/catalog" if name == "xur_catalog" else args["path"], args.get("body"))
        return reply(dict(content=[dict(type="text", text=json.dumps(result))], isError=not 200 <= result["status"] < 300))
    except ValueError as exc:
        return reply(dict(content=[dict(type="text", text=str(exc))], isError=True))
    except Exception:
        # Never return credentials or raw connection exceptions to the model.
        return reply(dict(content=[dict(type="text", text="Xur unavailable. Check T3 permissions, connection and certificate; reconnect MCP if needed.")], isError=True))


def main():
    while True:
        line = sys.stdin.buffer.readline(LIMIT + 1)
        if not line:
            return
        if len(line) > LIMIT:
            return
        try:
            response = dispatch(json.loads(line))
        except (ValueError, TypeError):
            response = dict(jsonrpc="2.0", id=None, error=dict(code=-32700, message="Parse error"))
        if response is not None:
            print(json.dumps(response), flush=True)


if __name__ == "__main__":
    main()
