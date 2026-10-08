import copy
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]


class BenchmarkTests(unittest.TestCase):
    def setUp(self):
        path=ROOT/"benchmark.py"
        if not path.exists(): self.fail("Benchmark feature missing")
        spec=importlib.util.spec_from_file_location("harness_benchmark",path); self.api=importlib.util.module_from_spec(spec); spec.loader.exec_module(self.api)
        self.tmp=tempfile.TemporaryDirectory(); self.addCleanup(self.tmp.cleanup); self.target=Path(self.tmp.name)/"result.json"; self.target.write_bytes(b'previous-valid-evidence')

    def measure(self,command,samples):
        return dict(implementation=command[0],version="1.0.0",samples=samples,elapsedMilliseconds=[float(i+1) for i in range(samples)],
                    profile=dict(memoryKiB=65536,iterations=3,parallelism=4,length=32),environment=dict(os="Linux",architecture="x86_64",runtime="fixture-runtime",library="fixture-library"),
                    peakRssBytes=1000000,rssMethod="Linux wait4 ru_maxrss KiB converted to bytes; includes runtime/JIT baseline")

    def run_benchmark(self,measure=None,samples=10):
        with patch.object(self.api,"measure",side_effect=measure or self.measure): return self.api.run(["java"],["python"],samples,self.target)

    def test_GivenCompleteSamples_WhenMeasured_ThenProfileAndStatisticsRecorded(self):
        result=self.run_benchmark(); self.assertEqual(65536,result["java"]["profile"]["memoryKiB"]); self.assertEqual(10,result["python"]["samples"])
        self.assertEqual(5.5,result["python"]["statisticsMilliseconds"]["median"]); self.assertEqual(10,result["python"]["statisticsMilliseconds"]["p95"])
        self.assertTrue(all(t>0 for t in result["python"]["elapsedMilliseconds"]))

    def test_GivenChildFailure_WhenMeasured_ThenPreviousEvidencePreserved(self):
        def measure(command,samples):
            if command[0]=="java": raise FileNotFoundError("marker-secret")
            return self.measure(command,samples)
        with self.assertRaises(self.api.HarnessFailure) as error: self.run_benchmark(measure)
        self.assertNotIn("marker-secret",str(error.exception)); self.assertEqual(b'previous-valid-evidence',self.target.read_bytes())

    def test_GivenInvalidSamples_WhenMeasured_ThenRejected(self):
        for samples in [0,-1,True,1.0,"10"]:
            with self.subTest(samples=samples),self.assertRaises(self.api.HarnessFailure): self.run_benchmark(samples=samples)
        self.assertEqual(b'previous-valid-evidence',self.target.read_bytes())

    def test_GivenIncompleteDuplicateBogusOrSecretBearingMetrics_WhenMeasured_ThenNoArtifact(self):
        for mutation in ["short","duplicate","negative","nan","rss","units","environment","profile","secret"]:
            def measure(command,samples):
                result=self.measure(command,samples)
                if mutation=="short": result["elapsedMilliseconds"].pop()
                elif mutation=="duplicate": result["elapsedMilliseconds"].append(1.0)
                elif mutation=="negative": result["elapsedMilliseconds"][0]=-1
                elif mutation=="nan": result["elapsedMilliseconds"][0]=float('nan')
                elif mutation=="rss": result["peakRssBytes"]=0
                elif mutation=="units": result["rssMethod"]="unknown"
                elif mutation=="environment": result["environment"].pop("library")
                elif mutation=="profile": result["profile"]["memoryKiB"]=32
                elif mutation=="secret": result["privateKey"]="marker-secret"
                return result
            with self.subTest(mutation=mutation),self.assertRaises(self.api.HarnessFailure) as error: self.run_benchmark(measure)
            self.assertNotIn("marker-secret",str(error.exception)); self.assertEqual(b'previous-valid-evidence',self.target.read_bytes())
