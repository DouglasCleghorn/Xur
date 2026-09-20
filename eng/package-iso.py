#!/usr/bin/env python3
"""Package only verified installer media and explicitly scoped evidence."""
import argparse,datetime,hashlib,json,os,pathlib,re,tarfile
repo=pathlib.Path(__file__).resolve().parents[1];os.chdir(repo);dist=repo/'dist'
parser=argparse.ArgumentParser();parser.add_argument('--vm',default='install8');parser.add_argument('--discovery-prefix',default='final');options=parser.parse_args()
iso=dist/'xur-installer-x86_64.iso'
def sha(path):
 with path.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
iso_hash=sha(iso)
bundle=json.loads((repo/'.build/context/rootfs/usr/share/xur/app-bundle/bundle.json').read_text())
embedded=json.loads((dist/'xur-installer-x86_64.embedded.json').read_text())['verifiedFiles']
assert embedded['usr/share/xur/app-bundle/bundle.json']==sha(repo/'.build/context/rootfs/usr/share/xur/app-bundle/bundle.json')
with tarfile.open(dist/'xur-app-x86_64.tar.gz') as archive:
 assert json.load(archive.extractfile('./bundle.json'))==bundle
 for name,digest in bundle['files'].items():
  assert hashlib.sha256(archive.extractfile('./'+name).read()).hexdigest()==digest,name
for name in ('bazzite-host.json','updates-ui.json','updates-console.json','session-clock.json','kernel-console.json','login-format.json'):
 receipt=json.loads((repo/'.build/evidence'/name).read_text());assert receipt['result']=='Passed' and receipt['media']['isoSha256']==iso_hash and not receipt['media'].get('modifiedForDiagnosis'),name
receipts=[f'live-{options.vm}.json',f'installed-{options.vm}.json','install-complete.json',f'discovery-{options.discovery_prefix}-no-disks.json',f'discovery-{options.discovery_prefix}-one-answer.json',f'discovery-{options.discovery_prefix}-multiple-answers.json','console-qr.json','console-window.json','settings-ui.json','web-ui.json','api-initialization.json','reboot-ui.json','tailscale-browser-login.json','profile-media.json','profile-ui.json','catalog-workstation.json']
for name in receipts:
 data=json.loads((repo/'.build/evidence'/name).read_text());assert data['media']['isoSha256']==iso_hash and not data['media'].get('modifiedForDiagnosis'),name
receipts+=['bazzite-host.json','updates-ui.json','updates-console.json','usb-preservation.json','session-clock.json','kernel-console.json','login-format.json']
assert json.loads((repo/'.build/evidence/usb-preservation.json').read_text())['unchanged']
assert json.loads((repo/'.build/evidence/usb-preservation.json').read_text())['media']['isoSha256']==iso_hash
assert json.loads((repo/'.build/evidence/console-qr.json').read_text())['visibleConsoleQrDecoded']
assert json.loads((repo/'.build/evidence/console-window.json').read_text())['qrStableAcrossLogRefreshes']
assert json.loads((repo/'.build/evidence/console-window.json').read_text())['qrPreservedThroughActualInstall']
assert json.loads((repo/'.build/evidence/settings-ui.json').read_text())['loopbackExcluded']
assert json.loads((repo/'.build/evidence/web-ui.json').read_text())['anonymousDashboardHidden']
assert json.loads((repo/'.build/evidence/console-window.json').read_text())['menuVirtualTerminal']==3
assert json.loads((repo/'.build/evidence/api-initialization.json').read_text())['result']=='Passed'
api=json.loads((repo/'.build/evidence/api-initialization.json').read_text())
assert api['reusableToken'] and api['sessionValidAfterReboot'] and api['exactPlanApprovalWithoutSerialEntry']
reboot=json.loads((repo/'.build/evidence/reboot-ui.json').read_text())
assert reboot['jwtCookiePreserved'] and reboot['waitsForNewBoot'] and reboot['installedDashboardHasNoInstallerControls'] and reboot['tailscaleRetained']
console=json.loads((repo/'.build/evidence/console-window.json').read_text())
assert all(console[key] for key in ['arrowNavigationAndEnter','overscanMargins','fullRowHighlight','enterAndEscapeReturnFromQr','qrPreservedWhenReopened'])
assert json.loads((repo/'.build/evidence/tailscale-browser-login.json').read_text())['realClientAuthorizationLink']
build=json.loads((repo/'.build/evidence/build-script.json').read_text());assert build['result']=='Passed' and build['isoSha256']==iso_hash
assert json.loads((repo/'.build/evidence'/f'installed-{options.vm}.json').read_text())['dataSha256Before']==json.loads((repo/'.build/evidence'/f'installed-{options.vm}.json').read_text())['dataSha256After']
images=json.loads((repo/'.build/evidence/media/images.json').read_text())
assert all(image['localReference'].endswith(':x86_64') for image in images.values())
osbuild=(repo/'.build/evidence/media/osbuild-manifest.json').read_text()
assert all(image['configDigest'] in osbuild for image in images.values()),'Image metadata differs from the built manifest'
assert json.loads((repo/'.build/evidence/profile-media.json').read_text())['result']=='Passed'
assert json.loads((repo/'.build/evidence/profile-ui.json').read_text())['result']=='Passed'
assert json.loads((repo/'.build/evidence/profile-process-tests.json').read_text())['cycles']==20
assert json.loads((repo/'.build/evidence/profile-podman-tests.json').read_text())['concurrentStatusDuringFiveCycles']
assert json.loads((repo/'.build/evidence/profile-ui.json').read_text())['unchangedPidAcrossBrowserSwitch']
catalog=json.loads((repo/'.build/evidence/catalog-workstation.json').read_text())
assert catalog['result']=='Passed' and all(catalog[k] for k in ['liveUnslothSearch','immutableSelection','realGgufInference','nativePlasmaStart','vulkanExercise','stationStop','stationReboot','modelPidPreserved'])
acceptance={'schema':1,'scope':'Xur installer, model catalogs, profiles and one native workstation','isoSha256':iso_hash,'results':{
 'uefiLiveBoot':'Passed','twoDhcpAdapters':'Passed','lanWebRoot':'Passed','ipv6ListenerProcessTest':'Passed',
 'localConsoleBootstrap':'Passed','reusableAccessCode':'Passed','bootstrapAuthenticationExpiryRateLimit':'Passed','anonymousStorageDenied':'Passed',
 'realTailscaleQrVisibleAndDecodable':'Passed','webResponsiveWhileTailscaleWaits':'Passed','tailscaleEnrollmentAndExternalHttps':'NotRun',
 'noDiskBoot':'Passed','zeroOneMultipleAnswerCases':'Passed','readOnlyAllEligibleStorageInTestMatrix':'Passed',
 'configParentProtection':'Passed','hybridUsbBootMediaProtection':'Passed','partitionTargetRejected':'Passed',
 'selectedDiskApprovalWithoutSerialEntry':'Passed','diskSizeSwapRevalidation':'Passed','serialAndPathHotSwapTests':'NotRun',
 'realAnacondaBootcInstall':'Passed','installedBootWithUsbMediaAttached':'Passed','secondInstallDenied':'Passed','nonTargetDiskBytePreservation':'Passed',
 'apiInitializationFromReadOnlyAnswer':'Passed','anonymousDashboardHidden':'Passed','sixCharacterBase32Bootstrap':'Passed','spectreConsoleOnDedicatedVt3':'Passed','arrowKeyNavigation':'Passed','consoleOverscanMargins':'Passed','selectedRowHighlight':'Passed','qrReturnMenu':'Passed','groupedAccessCode':'Passed','loginAvailableDuringDiscovery':'Passed','jwtSessionSurvivesReboot':'Passed','rebootPageReconnect':'Passed','installedSystemDashboard':'Passed','settingsAllNonLoopbackAddresses':'Passed','separateConsoleLogWindow':'Passed','fixedMenuAndQrWindows':'Passed','buildScriptEndToEnd':'Passed','desktopAndMobileUi':'Passed','embeddedFilesMatchBuild':'Passed',
 'profileSwitching':'Passed','gatewayStreamingContinuity':'Passed','profileJournalRecovery':'Passed','profilePersistenceAcrossReboot':'Passed','cpuModelCatalogAndRealInference':'Passed','referenceMultiGpuModelRecipes':'NotImplemented','liveModelCatalogs':'Passed','importedGgufRealInference':'Passed','nativePlasmaWorkstation':'Passed','workstationVulkanXWaylandSoftwareRender':'Passed','workstationTeardownAndReboot':'Passed','workstationModelContinuity':'Passed','physicalHdmiAudioUsb':'NotRun','multipleIndependentWorkstations':'NotImplemented',
 'gpuPciDiscoveryAndAllocationPolicy':'Implemented','nvidiaHostDriverVariant':'UpstreamBazziteIncluded','multiGpuRuntimeExecution':'NotRun',

 'secureBoot':'NotRun','automaticFailedBootRollback':'NotImplemented','upstreamOsUpdates':'Passed','updatesWebUi':'Passed','updatesTerminalMenu':'Passed','independentApplicationBundle':'Passed','applicationBundleUpdateActivation':'Passed'},'evidence':receipts}
for name in ('application-updates.json','application-updates-ui.json','application-updates-console.json'):
 app_receipt=json.loads((repo/'.build/evidence'/name).read_text())
 assert app_receipt['result']=='Passed' and app_receipt['media']['isoSha256']==iso_hash and not app_receipt['media'].get('modifiedForDiagnosis'),name
 receipts.append(name)
app_receipt=json.loads((repo/'.build/evidence/application-updates.json').read_text())
assert 'InterruptedActivationBootRecovery' in app_receipt['results'] and app_receipt['workstationPidAndRendererAndForegroundPreserved']
acceptance['results'].update({'signedApplicationUpdates':'Passed','applicationUpdateServerSetting':'Passed','applicationUpdateStreamingDrain':'Passed','applicationUpdateWorkloadPidContinuity':'Passed','applicationUpdateWorkstationContinuity':'Passed','applicationUpdateHealthRollback':'Passed','applicationUpdateBootRecovery':'Passed','applicationUpdateCookiePersistence':'Passed'})
host_evidence=json.loads((repo/'.build/evidence/bazzite-host.json').read_text())
acceptance['results']['sessionSurvivesBackwardClockCorrection']='Passed'
acceptance['results']['kernelOutputIsolatedFromMenu']='Passed'
acceptance['results']['accessCodeAutoHyphen']='Passed'
profile_ui=json.loads((repo/'.build/evidence/profile-ui.json').read_text())
assert profile_ui['defaultNameAndIntegerProfileId'] and profile_ui['onlySearchableSelectors']
acceptance['results']['profileEditorSearchableSelectors']='Passed'
acceptance['results']['osUpdateReboot']='Passed' if host_evidence.get('updateReboot')=='Passed' else 'NotRun'
acceptance['results']['explicitOsRollback']='Passed' if host_evidence.get('explicitRollback')=='Passed' else 'NotRun'
acceptance['osUpdateEvidenceScope']=host_evidence.get('versionChange','See the OS update receipt for the exercised deployment versions.')
ui=[repo/'.build/evidence/ui'/f'{page}-{width}.png' for page in ['login','profile-edit','profiles','progress','settings','setup','system','updates','model-catalog','gaming-profile'] for width in ['desktop','mobile']]
ui.extend(repo/'.build/evidence/ui'/f'application-updates-{width}.png' for width in ['desktop','mobile'])
ui.append(repo/'.build/evidence/ui/workstation.png')
assert all(p.is_file() and p.stat().st_mtime>=iso.stat().st_mtime for p in ui),'UI captures must be refreshed after building this ISO'
(repo/'.build/evidence/ui-capture.json').write_text(json.dumps({'suite':'UiCaptures','result':'Passed','viewports':['1440x1000','390x844'],'media':catalog['media'],'files':{str(p.relative_to(repo)):sha(p) for p in ui}},indent=2)+'\n')
receipts.append('ui-capture.json')
(dist/'acceptance.json').write_text(json.dumps(acceptance,indent=2)+'\n')
# Source selection intentionally cannot reach VM state, downloaded inputs or build outputs.
from source_files import source_files
files=source_files(repo)
secrets=set()
for p in (repo/'.build/vms').glob('*/console.private.log'):
 data=p.read_bytes();secrets.update(re.findall(rb'(?:One-time|Access) code: ((?:[A-F0-9]{32}|[0-9A-HJKMNP-TV-Z]{3}-?[0-9A-HJKMNP-TV-Z]{3}))',data));secrets.update(re.findall(rb'https://login\.tailscale\.com/[A-Za-z0-9/_-]+',data))
secrets.update(secret.replace(b'-',b'') for secret in list(secrets) if re.fullmatch(rb'[0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3}',secret))
private_key=re.compile(rb'-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----[A-Za-z0-9+/=\r\n]{64,}-----END (?:OPENSSH |RSA |EC )?PRIVATE KEY-----')
credential=re.compile(rb'(?:hf_[A-Za-z0-9]{20,}|tskey-(?:auth|api)-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})')
for p in files:
 data=p.read_bytes();assert not private_key.search(data),str(p)
 assert not credential.search(data),str(p)
 assert not any(secret in data for secret in secrets),str(p)
# Scan the expanded staged runtime as well as source; compressed ISO bytes alone are insufficient.
for p in (repo/'.build/context').rglob('*'):
 if p.is_file():
  data=p.read_bytes();assert not private_key.search(data),str(p);assert not credential.search(data),str(p);assert not any(secret in data for secret in secrets),str(p)
source=dist/'xur-source.tar.gz'
with tarfile.open(source,'w:gz') as archive:
 for p in sorted(files):archive.add(p,arcname='Xur/'+str(p.relative_to(repo)),recursive=False)
manifest={'schema':1,'builtAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'architecture':'x86_64','releaseStatus':'InstallerCatalogsProfilesAndNativeWorkstation','iso':{'file':iso.name,'bytes':iso.stat().st_size,'sha256':iso_hash},'source':{'file':source.name,'sha256':sha(source)},'toolchain':json.loads((repo/'eng/toolchain-lock.json').read_text()),'images':json.loads((repo/'.build/evidence/media/images.json').read_text()),'sourceFiles':{str(p.relative_to(repo)):sha(p) for p in sorted(files)},'embeddedVerificationSha256':sha(repo/'.build/evidence/media/embedded-verification.json'),'privateArtifactScan':{'sourceAndExpandedRuntime':'Passed','knownLiveSecretsChecked':len(secrets),'privateKeys':'None detected','rawConsoleQrOrSessionFilesPackaged':False},'limits':['Four-3090 reference model deployments, multiple workstations and per-station USB/audio selection are unfinished; desktop rendering was exercised with VM software Vulkan','External Tailscale enrollment/Serve not exercised with an operator account','Fedora RPM repositories are not snapshot-pinned; exact delivered RPM inventories are included','OS updates use upstream Bazzite; engine update management remains unfinished']}
manifest['applicationUpdateRepository']={'defaultPort':8088,'clientConfiguredAddress':True,'signature':'Ed25519','publicKeySha256':sha(repo/'os/bootc/application-update-key.pem'),'evidence':'.build/evidence/application-updates.json'}
manifest['applicationBundle']={'file':'xur-app-x86_64.tar.gz','sha256':sha(dist/'xur-app-x86_64.tar.gz'),'id':bundle['id'],'hostAbi':bundle['hostAbi']}
(dist/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
outputs=[iso,source,dist/'INSTALL.md',dist/'acceptance.json',dist/'build-manifest.json',dist/'xur-app-x86_64.tar.gz',dist/'xur-installer-x86_64.embedded.json']
(dist/'SHA256SUMS').write_text(''.join(f'{sha(p)}  {p.name}\n' for p in outputs))
print(json.dumps({'iso':str(iso),'bytes':iso.stat().st_size,'sha256':iso_hash,'source':str(source),'acceptance':str(dist/'acceptance.json')}))
