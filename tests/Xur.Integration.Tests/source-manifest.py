import importlib.util,tempfile,pathlib
spec=importlib.util.spec_from_file_location('source_files',pathlib.Path(__file__).resolve().parents[2]/'eng/source_files.py')
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
with tempfile.TemporaryDirectory() as tmp:
 root=pathlib.Path(tmp)
 keep=['AGENTS.md','README.md','src/App.cs','src/wwwroot/icon.png','os/bootc/application-update-key.pem','docs/usage/help.md','eng/.env.example']
 omit=['docs/evidence/capture.json','src/obj/build.json','src/node_modules/lib/index.js','src/local.private.json','src/.env','src/private.pem','tests/crash.core','.build/evidence/check.json','dist/release.iso']
 for name in keep+omit:
  p=root/name;p.parent.mkdir(parents=True,exist_ok=True);p.write_text('fixture')
 assert {str(p.relative_to(root)) for p in module.source_files(root)}==set(keep)
print('Source manifest excludes artifacts and secrets without requiring Git')
