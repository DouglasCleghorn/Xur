#!/usr/bin/env python3
"""Synthetic GPU/cache preparation tests; no robot devices or GPU jobs."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("gr00t_server", ROOT / "tools/Xur.Robotics/gr00t/server.py")
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class Preparation(unittest.TestCase):
    def test_benchmark_is_not_a_robot_embodiment(self):
        self.assertEqual(server.configuration({})["embodiment_tag"], server.DROID)
        self.assertTrue(server.configuration({})["strict"])
        with self.assertRaises(ValueError):
            server.configuration({"GR00T_MODE": "custom"})
        with self.assertRaises(ValueError):
            server.configuration({"GR00T_MODE": "custom", "GR00T_CHECKPOINT": "/cache/models//n1.7/"})
        self.assertEqual(server.configuration({"GR00T_MODE": "custom", "GR00T_CHECKPOINT": "/cache/models/fine-tuned"})["embodiment_tag"], "NEW_EMBODIMENT")

    def test_server_requires_token_and_removes_remote_kill(self):
        seen = {}
        class Policy:
            def __init__(self, **kwargs): seen["policy"] = kwargs
        class Transport:
            def __init__(self, **kwargs):
                seen["server"] = kwargs
                self._endpoints = {"kill": object(), "ping": object()}
            def __enter__(self): return self
            def __exit__(self, *args): pass
            def run(self): seen["killRemoved"] = "kill" not in self._endpoints
        modules = {
            "gr00t.policy.server_client": types.SimpleNamespace(PolicyClient=object, PolicyServer=Transport),
            "gr00t.policy.gr00t_policy": types.SimpleNamespace(Gr00tPolicy=Policy),
            "gr00t.data.embodiment_tags": types.SimpleNamespace(EmbodimentTag=types.SimpleNamespace(resolve=lambda value: value)),
        }
        with patch.dict(sys.modules, modules), patch.object(server, "token", return_value="x" * 64), patch.object(sys, "argv", ["server", "serve"]), patch.dict(os.environ, {}, clear=True):
            server.main()
        self.assertTrue(seen["killRemoved"])
        self.assertEqual(seen["server"]["api_token"], "x" * 64)
        self.assertTrue(seen["policy"]["strict"])

    def test_paths_and_modes_reject_remote_or_traversing_checkpoints(self):
        for value in ("/tmp/model", "nvidia/GR00T-N1.7-3B", "/cache/models/../../model"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                server.configuration({"GR00T_CHECKPOINT": value})
        with self.assertRaises(ValueError):
            server.configuration({"GR00T_MODE": "robot"})

    def test_authentication_cannot_be_empty_or_weak(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            p = Path(name) / "secret"
            for value in ("", "weak", "x" * 32 + " bad", "x" * 513):
                p.write_text(value)
                with self.subTest(length=len(value)), self.assertRaises(ValueError): server.token(p)
            p.write_text("x" * 64 + "\n")
            self.assertEqual(server.token(p), "x" * 64)

    def test_pinned_cache_receipt_and_backbone_offline_ref(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name); secret = root / "secret"; secret.write_text("x" * 64)
            revision = root / "revision"; revision.write_text("reviewed-source")
            calls = []
            def download(repo, **kwargs):
                calls.append(kwargs["revision"])
                p = Path(kwargs.get("local_dir", root / "hub/models--nvidia--Cosmos-Reason2-2B/snapshots" / kwargs["revision"]))
                p.mkdir(parents=True, exist_ok=True)
                return str(p)
            with patch.dict(sys.modules, {"huggingface_hub": types.SimpleNamespace(snapshot_download=download)}), patch.dict(os.environ):
                server.prepare_cache(root / "cache", secret, revision)
            self.assertEqual(calls, [server.MODEL_REVISION, server.COSMOS_REVISION])
            self.assertEqual((root / "hub/models--nvidia--Cosmos-Reason2-2B/refs/main").read_text(), server.COSMOS_REVISION)
            receipt = (root / "cache/prepared.json").read_text()
            self.assertFalse(json.loads(receipt)["gpuTested"])
            self.assertFalse(json.loads(receipt)["robotCompatible"])
            self.assertNotIn("x" * 64, receipt)

    def test_access_failure_removes_stale_preparation_receipt(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name); secret = root / "secret"; secret.write_text("x" * 64)
            (root / "prepared.json").write_text('{"ready":true}')
            def denied(*args, **kwargs): raise PermissionError("Gated access missing")
            with patch.dict(sys.modules, {"huggingface_hub": types.SimpleNamespace(snapshot_download=denied)}), patch.dict(os.environ):
                with self.assertRaises(PermissionError): server.prepare_cache(root, secret, root / "revision")
            self.assertFalse((root / "prepared.json").exists())

    def test_unexpected_backbone_revision_does_not_create_receipt(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name); secret = root / "secret"; secret.write_text("x" * 64)
            def wrong(*args, **kwargs): return str(root / "wrong")
            with patch.dict(sys.modules, {"huggingface_hub": types.SimpleNamespace(snapshot_download=wrong)}), patch.dict(os.environ):
                with self.assertRaises(RuntimeError): server.prepare_cache(root, secret, root / "revision")
            self.assertFalse((root / "prepared.json").exists())


@unittest.skipUnless(os.environ.get("XUR_DOTNET"), "Set XUR_DOTNET for the .NET console checks")
class Console(unittest.TestCase):
    def invoke(self, *args, ok=True):
        command = [os.environ["XUR_DOTNET"], "run", "--file", "tools/Xur.Robotics/gr00t/prepare.cs", "--artifacts-path", ".build/robotics/gr00t-cli", "--", *map(str, args)]
        r = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=120)
        self.assertEqual(r.returncode == 0, ok, r.stdout + r.stderr)
        return r

    def test_plan_reuses_private_credential_and_never_enables_execution(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name)
            self.invoke("plan", root); secret = (root / "policy-token").read_text()
            self.invoke("plan", root)
            self.assertEqual((root / "policy-token").read_text(), secret)
            if os.name != "nt": self.assertEqual((root / "policy-token").stat().st_mode & 0o777, 0o600)
            c = json.loads((root / "connection.json").read_text())
            self.assertFalse(c["executionEnabled"])
            self.assertFalse(c["jointCalibrationApproved"])
            self.assertFalse(c["robotAdapterConfigured"])
            self.assertEqual(c["host"], "127.0.0.1")
            self.assertNotIn(secret.strip(), (root / "preparation.json").read_text())

    def test_per_gpu_capacity_not_summed_vram(self):
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name); csv = root / "gpus.csv"
            csv.write_text("GPU-aaaaaaaa, RTX 3070, 8192, 8192, 580.65.06, 8.6\nGPU-bbbbbbbb, RTX 3070, 8192, 8192, 580.65.06, 8.6\n")
            self.invoke("inventory", csv, root)
            self.assertFalse(json.loads((root / "inventory-assessment.json").read_text())["inferenceHardwareCandidate"])
            csv.write_text("GPU-aaaaaaaa, RTX 3090, 24576, 23000, 580.65.06, 8.6\n")
            self.invoke("inventory", csv, root)
            a = json.loads((root / "inventory-assessment.json").read_text())
            self.assertTrue(a["inferenceHardwareCandidate"])
            self.assertFalse(a["fineTuningHardwareCandidate"])
            self.assertFalse(a["gpuInferenceTested"])

    def test_invalid_inventory_and_generated_source_output_rejected(self):
        self.invoke("plan", "containers/gr00t/generated", ok=False)
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name); csv = root / "gpus.csv"
            csv.write_text("GPU-aaaaaaaa, GPU, 16000, 20000, 580.65, 8.6\n")
            self.invoke("inventory", csv, root, ok=False)

    def test_nominal_capacity_driver_architecture_and_available_memory(self):
        cases = (
            ("16376, 16300, 580.65.06, 8.6", True, False),
            ("40536, 40300, 580.65.06, 8.0", True, True),
            ("24576, 23000, 550.54.14, 8.6", False, False),
            ("32768, 30000, 580.65.06, 7.0", False, False),
            ("24576, 12000, 580.65.06, 8.6", False, False),
        )
        with tempfile.TemporaryDirectory(dir=ROOT / ".build") as name:
            root = Path(name); csv = root / "gpus.csv"
            for values, inference, training in cases:
                with self.subTest(values=values):
                    csv.write_text("GPU-aaaaaaaa, NVIDIA GPU, " + values + "\n")
                    self.invoke("inventory", csv, root)
                    assessment = json.loads((root / "inventory-assessment.json").read_text())
                    self.assertEqual(assessment["inferenceHardwareCandidate"], inference)
                    self.assertEqual(assessment["fineTuningHardwareCandidate"], training)


if __name__ == "__main__":
    (ROOT / ".build").mkdir(exist_ok=True)
    unittest.main(verbosity=2)
