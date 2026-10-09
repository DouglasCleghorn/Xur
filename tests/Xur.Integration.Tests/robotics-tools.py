#!/usr/bin/env python3
"""Check installed robotics dependencies offline, without opening robot devices."""
import importlib
import importlib.util
import subprocess
from pathlib import Path
import tempfile
import unittest


class ToolsContainer(unittest.TestCase):
    def test_native_marker_detector_and_bridge_reject_nonframes(self):
        import numpy as np
        spec = importlib.util.spec_from_file_location("xur_vision_check", "/opt/xur/bridge.py")
        bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(bridge)
        Path("/state/.build").mkdir(parents=True, exist_ok=True)
        result = bridge.detect_frame(np.full((128, 128, 3), 255, dtype=np.uint8))
        self.assertEqual(result, {"width": 128, "height": 128, "detections": []})
        self.assertFalse(list(Path("/state/.build").glob("*.pgm")))
        with self.assertRaises(RuntimeError):
            bridge.detect_frame(np.zeros((8, 8, 3), dtype=np.uint8))
        self.assertNotEqual(subprocess.run(["/opt/xur/detect-markers", "/missing.pgm"], capture_output=True).returncode, 0)

    def test_upstream_cli_modules_import(self):
        for module in ("lerobot_robot_xlerobot", "xlerobot_model.SO101Robot",
                       "lerobot.scripts.lerobot_train", "lerobot.scripts.lerobot_replay",
                       "lerobot.scripts.lerobot_rollout"):
            with self.subTest(module=module):
                importlib.import_module(module)

    def test_upstream_xbox_helpers_load_without_a_controller(self):
        spec = importlib.util.spec_from_file_location(
            "xur_tools_check_xbox", "/opt/XLeRobot/software/examples/5_xlerobot_teleop_xbox.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertTrue(callable(module.get_xbox_key_state))

    def test_cpu_act_can_compute_loss_and_infer_without_downloads(self):
        import torch
        from lerobot.configs.types import FeatureType, PolicyFeature
        from lerobot.policies.act.configuration_act import ACTConfig
        from lerobot.policies.act.modeling_act import ACTPolicy

        torch.set_num_threads(1)
        config = ACTConfig(
            input_features={"observation.state": PolicyFeature(FeatureType.STATE, (6,)),
                            "observation.images.head": PolicyFeature(FeatureType.VISUAL, (3, 64, 64))},
            output_features={"action": PolicyFeature(FeatureType.ACTION, (6,))},
            pretrained_backbone_weights=None, chunk_size=4, n_action_steps=4,
            dim_model=32, n_heads=4, dim_feedforward=64, n_encoder_layers=1,
            n_decoder_layers=1, use_vae=False)
        policy = ACTPolicy(config)
        observation = {"observation.state": torch.zeros(2, 6),
                       "observation.images.head": torch.zeros(2, 3, 64, 64)}
        batch = {**observation, "action": torch.zeros(2, 4, 6),
                 "action_is_pad": torch.zeros(2, 4, dtype=torch.bool)}
        policy.train()
        loss, _ = policy.forward(batch)
        self.assertTrue(torch.isfinite(loss).item())
        loss.backward()
        policy.eval()
        policy.reset()
        with torch.inference_mode():
            action = policy.select_action(observation)
        self.assertEqual(tuple(action.shape), (2, 6))
        self.assertTrue(torch.isfinite(action).all().item())

    def test_dataset_video_codec_round_trip(self):
        import av
        import numpy as np
        from torchcodec.decoders import VideoDecoder

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "synthetic.mp4"
            with av.open(str(path), mode="w") as video:
                stream = video.add_stream("libx264", rate=10)
                stream.width = stream.height = 64
                stream.pix_fmt = "yuv420p"
                for shade in (0, 64, 128, 255):
                    frame = av.VideoFrame.from_ndarray(
                        np.full((64, 64, 3), shade, dtype=np.uint8), format="rgb24")
                    for packet in stream.encode(frame):
                        video.mux(packet)
                for packet in stream.encode():
                    video.mux(packet)
            decoder = VideoDecoder(str(path), device="cpu")
            self.assertEqual(len(decoder), 4)
            self.assertEqual(tuple(decoder[0].shape), (3, 64, 64))


if __name__ == "__main__":
    unittest.main(verbosity=2)
