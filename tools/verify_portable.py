from pathlib import Path
from io import BytesIO
import datetime, hashlib, json, os, shutil, subprocess, time, uuid
import pefile
from PIL import Image
root=Path(__file__).resolve().parents[1]
exe=root/"dist/standalone/PILL.exe"
pe=pefile.PE(str(exe))
icons=[]; groups=0
for entry in pe.DIRECTORY_ENTRY_RESOURCE.entries:
    if entry.id==14: groups+=len(entry.directory.entries)
    if entry.id==3:
        for item in entry.directory.entries:
            data=item.directory.entries[0].data.struct
            with Image.open(BytesIO(pe.get_data(data.OffsetToData,data.Size))) as image:
                icons.append(image.size[0])
assert sorted(icons)==[16,20,24,32,40,48,64,128,256],icons
assert groups>=1
pe.close()
trial=root/"artifacts"/("portable-check-"+uuid.uuid4().hex[:8])
trial.mkdir(parents=True)
app=trial/"app";app.mkdir();shutil.copy2(exe,app/"PILL.exe")
assert [p.name for p in app.iterdir()]==["PILL.exe"]
output=trial/"preview";extract=trial/"bundle-extract";empty=trial/"no-dotnet";empty.mkdir()
env=os.environ.copy()
for key in ["DOTNET_ROOT","DOTNET_ROOT_X64","DOTNET_ROOT(x86)"]:env[key]=str(empty)
env["DOTNET_MULTILEVEL_LOOKUP"]="0"
env["PATH"]=str(Path(os.environ["SystemRoot"])/"System32")
env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]=str(extract)
env["PILL_PREVIEW_DIR"]=str(output)
env["DOTNET_HOST_TRACE"]="1"
env["DOTNET_HOST_TRACEFILE"]=str(trial/"host-trace.txt")
started=time.perf_counter()
result=subprocess.run([str(app/"PILL.exe"),"--preview-ui","--verify-native"],cwd=app,env=env,timeout=60,capture_output=True,text=True)
assert result.returncode==0,(result.returncode,result.stdout,result.stderr)
report=json.loads((output/"receipt.json").read_text())
assert report["windowIconLoaded"] and report["embeddedLicenseDocuments"]==7
assert not report["hardwareWrites"] and report["deviceAccess"]
trace=(trial/"host-trace.txt").read_text(encoding="utf-8")
print("HOSTTRACE",str(trial/"host-trace.txt"))
assert "Executing as a self-contained app" in trace,"Host did not confirm a self-contained app"
assert "is_framework_dependent=0" in trace,"Host reported a framework dependency"
assert "Completed new extraction." in trace,"Native bundle extraction was not confirmed"
native_files=sorted(p.name for p in extract.rglob("*.dll"))
assert {"PresentationNative_cor3.dll","wpfgfx_cor3.dll","D3DCompiler_47_cor3.dll"}.issubset(native_files),native_files
assert all((output/name).stat().st_size>1000 for name in report["files"])
receipt={"version":"0.2.1","at":datetime.datetime.now().astimezone().isoformat(),"exe_sha256":hashlib.sha256(exe.read_bytes()).hexdigest(),
 "exit_code":result.returncode,"elapsed_seconds":round(time.perf_counter()-started,2),"only_exe_copied":True,"dotnet_root_empty":True,
 "host_trace_recorded":True,"host_confirmed_self_contained":True,"framework_dependent":False,
 "extracted_native_files":native_files,"icon_sizes":sorted(icons),"icon_groups":groups,"window_icon_loaded":True,
 "embedded_license_documents":7,"previews_generated":len(report["files"]),"ble_scan_completed":True,"audio_enumeration_completed":True,
 "discovered_pills":report["discoveredPills"],"audio_endpoints":report["audioEndpoints"],"hardware_writes":False}
(root/"docs/portable-check-0.2.1.json").write_text(json.dumps(receipt,indent=2),encoding="utf-8")
print(json.dumps(receipt,indent=2))
