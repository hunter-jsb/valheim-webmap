# Serve a packaged web/ folder and forward everything else to the live mod:
# what a browser sees when the mod serves the site itself. Files in subfolders
# (js/, vendor/three/) are served from disk too, as the mod does.
import http.server, urllib.request, sys, os
ROOT, UP, PORT = os.path.realpath(sys.argv[1]), sys.argv[2], int(sys.argv[3])
class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=ROOT, **k)
    def do_GET(self):
        path = self.path.split("?")[0]
        if path in ("", "/"): path = "/index.html"
        local = os.path.realpath(os.path.join(ROOT, path.lstrip("/")))
        if local.startswith(ROOT + os.sep) and os.path.isfile(local) and "." in os.path.basename(local):
            self.path = path + (("?" + self.path.split("?",1)[1]) if "?" in self.path else "")
            return super().do_GET()
        try:
            with urllib.request.urlopen(UP + self.path, timeout=60) as r:
                body = r.read(); self.send_response(r.status)
                for k in ("Content-Type",):
                    v = r.headers.get(k);
                    if v: self.send_header(k, v)
                self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
        except urllib.error.HTTPError as e:
            self.send_response(e.code); self.end_headers()
        except Exception as e:
            self.send_response(502); self.end_headers()
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
