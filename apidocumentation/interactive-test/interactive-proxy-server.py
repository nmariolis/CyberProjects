import argparse
import json
import os
from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
from urllib.parse import parse_qs, urlencode, urlparse
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError


ALLOWED_METHODS = {"GET", "POST", "PUT", "PATCH", "DELETE"}
FORWARD_HEADERS = {
    "accept",
    "content-type",
    "integrationapikey",
    "authorization",
    "x-api-key",
    "user-agent",
}


class ProxyStaticHandler(SimpleHTTPRequestHandler):
    def __init__(self, *args, directory=None, **kwargs):
        super().__init__(*args, directory=directory, **kwargs)

    def do_OPTIONS(self):
        # Same-origin app; this keeps behavior explicit if browser sends OPTIONS.
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type, IntegrationApiKey, Authorization, X-API-Key")
        self.end_headers()

    def do_GET(self):
        if self._is_proxy_request():
            self._handle_proxy("GET")
            return
        super().do_GET()

    def do_POST(self):
        if self._is_proxy_request():
            self._handle_proxy("POST")
            return
        self.send_error(405, "Method Not Allowed")

    def do_PUT(self):
        if self._is_proxy_request():
            self._handle_proxy("PUT")
            return
        self.send_error(405, "Method Not Allowed")

    def do_PATCH(self):
        if self._is_proxy_request():
            self._handle_proxy("PATCH")
            return
        self.send_error(405, "Method Not Allowed")

    def do_DELETE(self):
        if self._is_proxy_request():
            self._handle_proxy("DELETE")
            return
        self.send_error(405, "Method Not Allowed")

    def _is_proxy_request(self):
        parsed = urlparse(self.path)
        return parsed.path.startswith("/__proxy")

    def _handle_proxy(self, method):
        if method not in ALLOWED_METHODS:
            self._json_error(405, "Unsupported method")
            return

        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        target = (query.get("target") or [""])[0].strip()

        if not target:
            self._json_error(400, "Missing target query parameter")
            return

        target_parsed = urlparse(target)
        if target_parsed.scheme not in ("http", "https"):
            self._json_error(400, "Target must be http or https")
            return

        content_length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(content_length) if content_length > 0 else None

        forward_headers = {}
        for name, value in self.headers.items():
            lname = name.lower()
            if lname in FORWARD_HEADERS:
                forward_headers[name] = value

        req = Request(target, data=body, method=method, headers=forward_headers)

        try:
            with urlopen(req, timeout=60) as upstream:
                response_body = upstream.read()
                self.send_response(upstream.status)

                content_type = upstream.headers.get("Content-Type", "application/json")
                self.send_header("Content-Type", content_type)
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
                self.wfile.write(response_body)
        except HTTPError as err:
            payload = err.read()
            self.send_response(err.code)
            self.send_header("Content-Type", err.headers.get("Content-Type", "application/json"))
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            if payload:
                self.wfile.write(payload)
            else:
                self.wfile.write(json.dumps({"error": str(err)}).encode("utf-8"))
        except URLError as err:
            self._json_error(502, f"Proxy connection failed: {err.reason}")
        except Exception as err:
            self._json_error(500, f"Proxy internal error: {err}")

    def _json_error(self, status, message):
        data = json.dumps({"error": message}).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(data)


def main():
    parser = argparse.ArgumentParser(description="SupplierAPI interactive static+proxy server")
    parser.add_argument("--port", type=int, default=8085)
    parser.add_argument("--root", default=os.getcwd())
    args = parser.parse_args()

    handler = lambda *h_args, **h_kwargs: ProxyStaticHandler(*h_args, directory=args.root, **h_kwargs)
    server = ThreadingHTTPServer(("", args.port), handler)

    print(f"Serving static root: {args.root}")
    print(f"Interactive page: http://localhost:{args.port}/interactive-test/index.html")
    print(f"Proxy endpoint:   http://localhost:{args.port}/__proxy?target=<url>")
    server.serve_forever()


if __name__ == "__main__":
    main()
