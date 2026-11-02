"""Container MCP protocol and persistent native configuration, without real secrets."""
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

setup = load('setup', 'src/Xur.Agent/t3-setup.py')

class Tests(unittest.TestCase):
    def test_native_registration_preserves_settings_and_is_idempotent(self):
        root = Path('.build/evidence/t3-mcp'); root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as folder:
            home = Path(folder)
            for provider, text, filename in [('codex','model = "keep"\n','config.toml'), ('claude','{"keep":true}','claude.json'), ('opencode','{"keep":true}','opencode.json')]:
                path = home / filename
                path.write_text(text)
                script = home / 'server'
                setup.mcp_registration(path, provider, script)
                first = path.read_text()
                setup.mcp_registration(path, provider, script)
                self.assertEqual(path.read_text(), first)
                self.assertIn('keep', first)
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                with self.assertRaises(setup.SetupError):
                    setup.mcp_registration(path, provider, home / 'other.py')

    def test_migrate_generated_python_registration(self):
        root = Path('.build/evidence/t3-mcp'); root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as folder:
            home = Path(folder)
            script = home / 'server'
            native = home / 'config.toml'
            native.write_text('model = "keep"\n[mcp_servers.xur]\ncommand = "python3"\nargs = [' + json.dumps(str(home / 'server.py')) + ']\n[mcp_servers.other]\ncommand = "keep"\n')
            setup.mcp_registration(native, 'codex', script)
            self.assertNotIn('python3', native.read_text())
            self.assertIn('[mcp_servers.other]', native.read_text())

    def test_container_setup_persists_private_connection_and_registers_all_harnesses(self):
        from unittest.mock import MagicMock
        root = Path('.build/evidence/t3-mcp'); root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as folder:
            home = Path(folder)
            setup.atomic(home / '.local/share/xur-mcp/server', '#!/bin/sh\n')
            connection = dict(origin='https://xur.test', apiKey='fixture-secret', certificate='fixture-cert')
            client = MagicMock()
            client.getresponse.return_value.status = 403  # Permissions may initially be disabled.
            with patch.object(setup, 'paths', return_value=(home, home / '.local/bin', home / '.t3/userdata/settings.json')), \
                    patch.object(setup.ssl, 'create_default_context'), \
                    patch.object(setup.http.client, 'HTTPSConnection', return_value=client):
                setup.configure_mcp(connection)
                setup.configure_mcp(connection)
            stored = home / '.local/share/xur-mcp/connection.json'
            self.assertEqual(json.loads(stored.read_text()), connection)
            self.assertEqual(stored.stat().st_mode & 0o777, 0o600)
            for native in (home / '.codex/config.toml', home / '.claude.json', home / '.config/opencode/opencode.json'):
                self.assertIn('/server', native.read_text())
                self.assertNotIn('fixture-secret', native.read_text())
            client.request.assert_called_with('GET', '/api/status', headers={'Authorization':'Bearer fixture-secret'})

if __name__ == '__main__':
    unittest.main()
