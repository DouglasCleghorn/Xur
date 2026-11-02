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

mcp = load('mcp', 'src/Xur.Agent/t3-mcp.py')
setup = load('setup', 'src/Xur.Agent/t3-setup.py')

class Tests(unittest.TestCase):
    def call(self, method, **params):
        return mcp.dispatch(dict(jsonrpc='2.0', id=3, method=method, params=params))

    def test_protocol(self):
        self.assertEqual(self.call('initialize', protocolVersion='2025-11-25')['result']['protocolVersion'], '2025-11-25')
        self.assertEqual(self.call('ping')['result'], {})
        self.assertIsNone(mcp.dispatch(dict(jsonrpc='2.0', method='notifications/initialized')))
        self.assertEqual(mcp.dispatch([])['error']['code'], -32600)
        self.assertEqual(self.call('unknown')['error']['code'], -32601)
        tools = self.call('tools/list')['result']['tools']
        self.assertEqual(len(tools), 3)
        self.assertFalse(tools[2]['annotations']['readOnlyHint'])

    def test_validation_and_permission_errors(self):
        for args in ({}, {'path': 3}, {'path':'/api/status','extra':1}):
            self.assertEqual(self.call('tools/call', name='xur_read', arguments=args)['error']['code'], -32602)
        with patch.object(mcp, 'request', return_value=dict(status=403, body='Permission disabled')) as request:
            result = self.call('tools/call', name='xur_write', arguments=dict(path='/api/timezone', method='POST', body=dict(timezone='UTC')))
            self.assertTrue(result['result']['isError'])
            request.assert_called_once_with('POST', '/api/timezone', {'timezone':'UTC'})
        for path in ('https://evil/api/status', '//evil/api/status', '/api/../auth', '/api/%2e%2e/auth', '/api/a\\b', '/api/status#fragment'):
            with self.assertRaises(ValueError):
                mcp.request('GET', path)
        with patch.object(mcp, 'request', side_effect=OSError('fixture-secret')):
            self.assertNotIn('fixture-secret', json.dumps(self.call('tools/call', name='xur_catalog')))

    def test_native_registration_preserves_settings_and_is_idempotent(self):
        root = Path('.build/evidence/t3-mcp'); root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as folder:
            home = Path(folder)
            for provider, text, filename in [('codex','model = "keep"\n','config.toml'), ('claude','{"keep":true}','claude.json'), ('opencode','{"keep":true}','opencode.json')]:
                path = home / filename
                path.write_text(text)
                script = home / 'server.py'
                setup.mcp_registration(path, provider, script)
                first = path.read_text()
                setup.mcp_registration(path, provider, script)
                self.assertEqual(path.read_text(), first)
                self.assertIn('keep', first)
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                with self.assertRaises(setup.SetupError):
                    setup.mcp_registration(path, provider, home / 'other.py')

    def test_container_setup_persists_private_connection_and_registers_all_harnesses(self):
        from unittest.mock import MagicMock
        root = Path('.build/evidence/t3-mcp'); root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=root) as folder:
            home = Path(folder)
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
                self.assertIn('server.py', native.read_text())
                self.assertNotIn('fixture-secret', native.read_text())
            client.request.assert_called_with('GET', '/api/status', headers={'Authorization':'Bearer fixture-secret'})

    def test_no_redirect_or_secret_in_harness_config(self):
        source = Path('src/Xur.Agent/t3-mcp.py').read_text()
        self.assertIn('http.client.HTTPSConnection', source)
        self.assertNotIn('urlopen', source)

if __name__ == '__main__':
    unittest.main()
