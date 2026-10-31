"""Local-only integration fixture. Uses only generated files and synthetic credentials."""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / '_tooltest' / 'deps'))
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler, ThrottledDTPHandler
from pyftpdlib.servers import FTPServer

root = pathlib.Path(sys.argv[1]).resolve()
root.mkdir(parents=True, exist_ok=True)
auth = DummyAuthorizer()
auth.add_user('tester', '空 格 & $ " \\ end', str(root), perm='elradfmwMT')
auth.add_user('keyword', 'object', str(root), perm='elradfmwMT')
class Data(ThrottledDTPHandler):
    read_limit = 512 * 1024
class Handler(FTPHandler):
    authorizer = auth
    dtp_handler = Data
    def ftp_RNFR(self, path):
        # Optional fault modes for backup compatibility regressions only.
        mode = sys.argv[3] if len(sys.argv) > 3 else ''
        if mode == 'reject-partial' and path.endswith('.partial') and 'reject-rename' in pathlib.Path(path).name:
            self.respond('553 "Can\'t rename file."')
            return
        return super().ftp_RNFR(path)
    def ftp_STOR(self, file, mode='w'):
        fault = sys.argv[3] if len(sys.argv) > 3 else ''
        if fault == 'reject-store' or (fault == 'reject-selected-store' and 'remote-error' in pathlib.Path(file).name):
            self.respond('550 Synthetic upload permission denied')
            return
        return super().ftp_STOR(file, mode)
server = FTPServer(('127.0.0.1', 0), Handler)
pathlib.Path(sys.argv[2]).write_text(str(server.socket.getsockname()[1]))
server.serve_forever()
