#!/usr/bin/env python3
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
spec = importlib.util.spec_from_file_location('explorer', Path(__file__).resolve().parents[2] / 'src/Xur.Agent/StorageExplorer.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ExplorerTests(unittest.TestCase):
    def test_allocated_hardlinks_and_symlinks(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root/'data').write_bytes(b'x'*8192)
            os.link(root/'data', root/'hardlink')
            (root/'outside').symlink_to('/etc', target_is_directory=True)
            report = module.scan(tmp, '')
            rows = {r['name']:r for r in report['entries']}
            self.assertEqual(rows['outside']['kind'], 'link')
            self.assertEqual(rows['data']['bytes'] + rows['hardlink']['bytes'], (root/'data').stat().st_blocks*512)
            self.assertFalse(report['partial'])
            with self.assertRaises(OSError):
                module.scan(tmp, 'outside')
            with self.assertRaises(ValueError):
                module.scan(tmp, '../etc')

    def test_partial_budget(self):
        with tempfile.TemporaryDirectory() as tmp:
            for i in range(12):
                Path(tmp,str(i)).write_bytes(b'x'*4096)
            report = module.scan(tmp, '', limit=2)
            self.assertTrue(report['partial'])
            self.assertEqual(len(report['entries']),12)
            self.assertTrue(any(r['bytes'] is None for r in report['entries']))

    def test_folder_drilldown(self):
        with tempfile.TemporaryDirectory() as tmp:
            Path(tmp,'models').mkdir()
            Path(tmp,'models','weights').write_bytes(b'x'*16384)
            report = module.scan(tmp,'models')
            self.assertEqual(report['entries'][0]['name'],'weights')
            self.assertGreaterEqual(report['bytes'],16384)


if __name__ == '__main__':
    unittest.main()
