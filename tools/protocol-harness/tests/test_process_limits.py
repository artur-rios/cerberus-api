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


def process_stopped(status):
    try:
        return status.read_text().split()[2] == 'Z'
    except (FileNotFoundError, ProcessLookupError):
        # A reaped descendant has no procfs entry, including if it vanishes on read.
        return True


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
            while not process_stopped(status) and time.monotonic()<deadline:
                time.sleep(0.01)
            self.assertTrue(process_stopped(status))
            self.assertFalse(self.marker.exists())
        finally:
            if pidfile.exists():
                try: os.kill(int(pidfile.read_text()),signal.SIGKILL)
                except ProcessLookupError: pass

    def test_GivenDescendantDisappearsDuringInspection_WhenCheckingTimeout_ThenAcceptTerminatedGroup(self):
        original_read = Path.read_text
        original_exists = Path.exists
        def exists(path):
            return True if str(path).startswith('/proc/') and path.name == 'stat' else original_exists(path)
        for exception in (FileNotFoundError, ProcessLookupError):
            with self.subTest(exception=exception.__name__):
                def read(path, *args, **kwargs):
                    if str(path).startswith('/proc/') and path.name == 'stat':
                        raise exception('descendant already reaped')
                    return original_read(path, *args, **kwargs)
                with patch.object(Path, 'exists', exists), patch.object(Path, 'read_text', read):
                    self.test_GivenTimedOutChildWithDescendant_WhenRunning_ThenWholeGroupStopped()

    def test_GivenLiveOrUnreadableDescendant_WhenCheckingTimeout_ThenNeverAcceptStoppedGroup(self):
        status=Path(self.tmp.name)/'stat'
        for state in ('S', 'R'):
            status.write_text(f'123 (python) {state} 0')
            self.assertFalse(process_stopped(status))
        status.write_text('123 (python) Z 0')
        self.assertTrue(process_stopped(status))
        with patch.object(Path, 'read_text', side_effect=PermissionError('unreadable procfs')):
            with self.assertRaises(PermissionError):
                process_stopped(status)

    def test_GivenBenchmarkOutputOrResultGrowth_WhenMeasured_ThenStoppedBeforeFurtherWork(self):
        benchmark=module('benchmark')
        for output in ("sys.stdout.buffer.write(b'x'*2097152); sys.stdout.flush()",
                       "pathlib.Path(sys.argv[-1]).write_bytes(b'x'*2097152)"):
            with self.subTest(output=output):
                code=f"import sys,time,pathlib; {output}; time.sleep(1); pathlib.Path({str(self.marker)!r}).touch()"
                with self.assertRaises(benchmark.HarnessFailure): benchmark.measure([sys.executable,'-c',code],1)
                self.assertFalse(self.marker.exists()); self.marker.unlink(missing_ok=True)
