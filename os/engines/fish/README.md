# Fish S2 Pro runtime

The pinned generic vLLM-Omni image does not include Fish Speech's DAC codec.
Xur prepares a derived image automatically when starting `fishaudio/s2-pro`.
Existing saved recipes with the same base digest are supported, including their
old Docker Hub spelling; image pulls use Google's mirror.

Dependencies are locked to exact archive URLs and SHA-256 hashes. Installation
does not resolve or replace base packages, and the build verifies all original
package versions remain unchanged. Audiotools’ legacy training protobuf constraint
is deliberately not applied to codec inference; compatibility is checked by
initializing the codec and by the recorded live speech test.

The dependency layer is cached under `localhost/xur-fish:<build-content-hash>`.
The agent inspects its immutable image ID and initializes the codec on CPU,
without network or GPU access, before creating the serving container. The model
runs with only its assigned GPU and retains the workload's persistent model
cache, pinned model revision, HF credentials and inference route.

Build failures appear in profile progress and are saved under the Xur state
folder's `fish-engine/<build-content-hash>/build.log`. Retry uses cached build
steps. Xur does not replace a different base engine with this tested dependency
layer; reselect Fish in the model editor after changing engine versions.

The codec packages retain their upstream licenses inside the image. Fish model
weights are downloaded separately and retain their own license. Neither is
relicensed by Xur's MIT license.
