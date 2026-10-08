"""Separate-process reference measurements; no performance acceptance threshold."""
import argparse
from datetime import datetime, timezone
import importlib.util
import math
import os
from pathlib import Path
import signal
import statistics
import subprocess
import sys
import tempfile
import time

_spec=importlib.util.spec_from_file_location("cerberus_measure_verify",Path(__file__).with_name("verify.py"))
_verify=importlib.util.module_from_spec(_spec); _spec.loader.exec_module(_verify)
HarnessFailure=_verify.HarnessFailure
PROFILE=dict(memoryKiB=65536,iterations=3,parallelism=4,length=32)
RSS_METHOD="Linux wait4 ru_maxrss KiB converted to bytes; includes runtime/JIT baseline"


def measure(command,samples):
    if sys.platform!="linux" or not hasattr(os,"wait4"): raise HarnessFailure("unsupported_dependency")
    with tempfile.TemporaryDirectory(prefix="cerberus-measure-") as directory:
        path=Path(directory); target=path/"result.json"
        with (path/"stdout").open("wb") as stdout,(path/"stderr").open("wb") as stderr:
            process=subprocess.Popen([*command,"benchmark",str(samples),str(target)],cwd=_verify.ROOT,
                env={**os.environ,"PYTHONPATH":str(_verify.HARNESS/"python")},stdout=stdout,stderr=stderr,start_new_session=True)
            deadline=time.monotonic()+300
            try:
                while True:
                    pid,status,usage=os.wait4(process.pid,os.WNOHANG)
                    if pid: break
                    if time.monotonic()>=deadline: raise HarnessFailure()
                    time.sleep(0.05)
            except BaseException:
                os.killpg(process.pid,signal.SIGKILL); _,status,_=os.wait4(process.pid,0); process.returncode=os.waitstatus_to_exitcode(status); raise
            process.returncode=os.waitstatus_to_exitcode(status)
            if process.returncode or any((path/name).stat().st_size>1048576 for name in ("stdout","stderr")): raise HarnessFailure()
        value=_verify.read_document(target); value.update(peakRssBytes=usage.ru_maxrss*1024,rssMethod=RSS_METHOD)
        return value


def _validated(value,implementation,samples):
    if type(value) is not dict or set(value)!={"implementation","version","samples","elapsedMilliseconds","profile","environment","peakRssBytes","rssMethod"}: raise HarnessFailure()
    if value["implementation"]!=implementation or value["version"]!="1.0.0" or value["samples"]!=samples or value["profile"]!=PROFILE: raise HarnessFailure()
    times=value["elapsedMilliseconds"]
    if type(times) is not list or len(times)!=samples or any(type(x) not in (int,float) or not math.isfinite(x) or x<=0 for x in times): raise HarnessFailure()
    if type(value["peakRssBytes"]) is not int or value["peakRssBytes"]<=0 or value["rssMethod"]!=RSS_METHOD: raise HarnessFailure()
    environment=value["environment"]
    if type(environment) is not dict or set(environment)!={"os","architecture","runtime","library"} or any(type(x) is not str or not x or len(x)>512 for x in environment.values()): raise HarnessFailure()
    ordered=sorted(times)
    value["statisticsMilliseconds"]=dict(min=min(times),median=statistics.median(times),p95=ordered[math.ceil(.95*samples)-1],max=max(times))
    return value


def run(java_command,python_command,samples,output):
    try:
        if type(samples) is not int or not 1<=samples<=1000 or not java_command or not python_command: raise HarnessFailure()
        java=_validated(measure(java_command,samples),"java",samples)
        python=_validated(measure(python_command,samples),"python",samples)
        result=dict(schemaVersion=1,classification="public-test-fixtures-reference-measurements-not-performance-acceptance",
                    measuredAtUtc=datetime.now(timezone.utc).isoformat(),warmupDerivations=1,java=java,python=python,
                    limitations="Linux desktop observations only; peak RSS includes runtime/JIT baseline; no latency SLO, isolated allocation or mobile compatibility claim.")
        _verify.atomic_write(output,result); return result
    except HarnessFailure: raise
    except FileNotFoundError: raise HarnessFailure("unsupported_dependency") from None
    except Exception: raise HarnessFailure() from None


def main():
    parser=argparse.ArgumentParser(); parser.add_argument("--samples",type=int,default=10); parser.add_argument("--output",type=Path,required=True); args=parser.parse_args()
    try:
        java,python=_verify.default_commands(); result=run(java,python,args.samples,args.output)
        print(_verify.json.dumps(dict(status="pass",samples=args.samples,javaMedianMs=result["java"]["statisticsMilliseconds"]["median"],pythonMedianMs=result["python"]["statisticsMilliseconds"]["median"]))); return 0
    except HarnessFailure as error: print(_verify.json.dumps(dict(code=error.code))); return 1


if __name__=="__main__": raise SystemExit(main())
