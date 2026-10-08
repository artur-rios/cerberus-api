import importlib.util
import os
from pathlib import Path
import signal
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

HARNESS=Path(__file__).resolve().parents[1]


def module(name):
    spec=importlib.util.spec_from_file_location('limits_'+name,HARNESS/(name+'.py'))
    value=importlib.util.module_from_spec(spec); spec.loader.exec_module(value); return value


class ProcessLimitTests(unittest.TestCase):
    def setUp(self):
        self.api=module('verify')
        self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup)
        self.marker=Path(self.tmp.name)/'completed'

    def test_GivenExcessChildOutput_WhenRunning_ThenStoppedBeforeFurtherWork(self):
        for stream in ('stdout','stderr'):
            with self.subTest(stream=stream),patch.object(self.api,'LIMIT',1024):
                code=f"import sys,time,pathlib; sys.{stream}.buffer.write(b'x'*2097152); sys.{stream}.flush(); time.sleep(1); pathlib.Path({str(self.marker)!r}).touch()"
                with self.assertRaises(self.api.HarnessFailure): self.api._run([sys.executable,'-c',code])
                self.assertFalse(self.marker.exists()); self.marker.unlink(missing_ok=True)

    def test_GivenGrowingResultFile_WhenInvoked_ThenStoppedBeforeFurtherWork(self):
        code=f"import sys,time,pathlib; pathlib.Path(sys.argv[-1]).write_bytes(b'x'*2097152); time.sleep(1); pathlib.Path({str(self.marker)!r}).touch()"
        with patch.object(self.api,'LIMIT',1024),self.assertRaises(self.api.HarnessFailure):
            self.api.invoke([sys.executable,'-c',code],'produce')
        self.assertFalse(self.marker.exists())

    def test_GivenTimedOutChildWithDescendant_WhenRunning_ThenWholeGroupStopped(self):
        pidfile=Path(self.tmp.name)/'pid'
        descendant=f"import time,pathlib; time.sleep(3); pathlib.Path({str(self.marker)!r}).touch()"
        code=f"import subprocess,sys,time,pathlib; child=subprocess.Popen([sys.executable,'-c',{descendant!r}],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL); pathlib.Path({str(pidfile)!r}).write_text(str(child.pid)); time.sleep(2)"
        try:
            start=time.monotonic()
            with patch.object(self.api,'PROCESS_TIMEOUT',0.3,create=True),self.assertRaises(self.api.HarnessFailure):
                self.api._run([sys.executable,'-c',code])
            self.assertLess(time.monotonic()-start,1.5)
            self.assertTrue(pidfile.exists())
            pid=int(pidfile.read_text()); status=Path(f'/proc/{pid}/stat')
            deadline=time.monotonic()+1
            while status.exists() and status.read_text().split()[2]!='Z' and time.monotonic()<deadline:
                time.sleep(0.01)
            self.assertTrue(not status.exists() or status.read_text().split()[2]=='Z')
            self.assertFalse(self.marker.exists())
        finally:
            if pidfile.exists():
                try: os.kill(int(pidfile.read_text()),signal.SIGKILL)
                except ProcessLookupError: pass

    def test_GivenBenchmarkOutputOrResultGrowth_WhenMeasured_ThenStoppedBeforeFurtherWork(self):
        benchmark=module('benchmark')
        for output in ("sys.stdout.buffer.write(b'x'*2097152); sys.stdout.flush()",
                       "pathlib.Path(sys.argv[-1]).write_bytes(b'x'*2097152)"):
            with self.subTest(output=output):
                code=f"import sys,time,pathlib; {output}; time.sleep(1); pathlib.Path({str(self.marker)!r}).touch()"
                with self.assertRaises(benchmark.HarnessFailure): benchmark.measure([sys.executable,'-c',code],1)
                self.assertFalse(self.marker.exists()); self.marker.unlink(missing_ok=True)
