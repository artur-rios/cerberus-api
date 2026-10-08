"""Independent Python public-fixture construction and execution; no Java imports."""
import copy
import hashlib
import json
from pathlib import Path

from . import challenge, keys, lease, primitives, protection, recipient, recovery_bundle
from .client_trust import ClientTrust
from .encoding import base64, unbase64, context, parse_object, MAX_REQUEST
from .errors import ProtocolError
from .lease_trust import LeaseTrust
from .offline_clock import OfflineClock
from .recovery_model import RecoveryModel
from .symmetric import Context, NonceGuard, seal, open_envelope
from .wire import encode

FIXTURES = Path(__file__).resolve().parents[2]/"fixtures"
ACCOUNT="00000000-0000-0000-0000-000000000001"
IDENTITY="00000000-0000-0000-0000-000000000002"
OTHER="00000000-0000-0000-0000-000000000008"
NOW=1700000000
ISSUER="https://cerberus.example.test"
RAW=b'{"idempotencyKey":"fixture","expectedRevision":1}'
ROOT=bytes(range(32)); SECRET=bytes(range(32,64)); PASSWORD=" vault-é "


def read(path): return parse_object(Path(path).read_bytes(), 64*1024*1024)
def write(path, value): Path(path).write_bytes(encode_large(value)+b'\n')
def encode_large(value): return json.dumps(value,sort_keys=True,separators=(",",":"),ensure_ascii=False,allow_nan=False).encode()
def digest(value): return hashlib.sha256(encode_large(value)).hexdigest()
def document(field, rows): return dict(schemaVersion=1,classification="public-test-fixtures",implementation="python",version="1.0.0",**{field:rows})
def inputs():
    value=read(FIXTURES/"input.json")
    if value["classification"]!="public-test-fixtures-never-production": raise ProtocolError()
    return value
def key(role):
    value=inputs()["keys"][role]
    return dict(public=value["publicJwk"],private=unbase64(value["privateDer"],len(value["privateDer"])*3//4))
def bundle(scope): return inputs()["bundles"][scope]
def membership(scope): return set(inputs()["membership"][scope])
def slot(kind,scope="account"):
    identity=bundle(scope)["scopeId"] if scope in ("account","profile") else "00000000-0000-0000-0000-000000000004"
    return Context(ACCOUNT,kind,identity,1,[])
def authority(enabled=True):
    return dict(iss=ISSUER,aud="cerberus-offline-clients-v1",sub=IDENTITY,accountId=ACCOUNT,scopeKind="account",scopeId=ACCOUNT,
                keyEpoch=1,protectionRevision=1,policyRevision=1,revocationGeneration=1,grantRevisions=[],renewalEnabled=enabled)
def binding(operation):
    profile=operation=="unlock-profile"
    return dict(operation=operation,identityId=IDENTITY,accountId=ACCOUNT,scopeKind="profile" if profile else "account",
                scopeId=bundle("profile")["scopeId"] if profile else ACCOUNT,keyEpoch=1,protectionRevision=1,generation=1 if operation=="recover" else None)
def role(operation): return "recovery-1" if operation=="recover" else "unlock-profile" if operation=="unlock-profile" else "unlock-account"


def _base(family):
    kind, name=family.split(".")
    if kind=="content": return seal(ROOT,slot(name,name),"cerberus-content-v1",b'public fixture plaintext',NonceGuard(0))
    if kind=="password": return protection.wrap(PASSWORD,bundle(name),slot(name+"-protection",name),NonceGuard(0))
    if kind=="recovery": return recovery_bundle.wrap(SECRET,bundle("account"),key("recovery-1")["private"],slot("recovery"),1,NonceGuard(0))
    if kind=="recipient": return recipient.wrap(ROOT,slot("record","record"),"00000000-0000-0000-0000-000000000007",1,IDENTITY,key("recipient")["public"],key("author")["private"])
    if kind=="challenge":
        issued=challenge.issue(binding(name),RAW,NOW)
        return dict(challenge=issued,proof=base64(challenge.sign(issued,key(role(name))["private"])),raw=base64(RAW))
    if kind=="lease": return dict(token=lease.sign(lease.claims(authority(name=="enabled"),NOW,name=="enabled",None),key("lease")["private"]))
    if kind=="encoding": return dict(bytes=base64(context(["cerberus-fixture-v1",ACCOUNT,1,True,None,[]])))
    if kind=="kdf": return dict(bytes=base64(primitives.argon2(PASSWORD.encode(),bytes(range(16)))))
    if kind=="hkdf": return dict(bytes=base64(primitives.hkdf(ROOT,bytes(32),b'cerberus-fixture-v1',32)))
    if kind=="signature": return dict(bytes=base64(keys.sign(key("author")["private"],b'public fixture signature')))
    if kind=="clock": return _clock(name)
    if kind=="model": return _model(name)
    raise ProtocolError()


def _changed(value):
    if type(value) is bool: return not value
    if type(value) is int: return True
    if value is None: return 1
    if type(value) is list: return [None]
    if type(value) is dict: return {}
    return "AA"


def _raw_token(claims,header):
    prefix=base64(encode_large(header))+"."+base64(encode_large(claims))
    return prefix+"."+base64(keys.sign(key("lease")["private"],prefix.encode()))


def produce(public_inputs):
    if public_inputs!=inputs(): raise ProtocolError()
    rows=[]; bases={}
    for rule in read(FIXTURES/"case-manifest.json")["cases"]:
        identity=rule["id"]; parts=identity.split("."); family=".".join(parts[:2]); kind=parts[0]
        if family not in bases: bases[family]=_base(family) if parts[1]!="invalid" else {}
        output=copy.deepcopy(bases[family])
        if kind=="encoding" and parts[1]=="invalid":
            invalid={"duplicate":b'{"x":1,"x":2}',"nested-duplicate":b'{"x":{"y":1,"y":2}}',"bom":b'\xef\xbb\xbf{}',"unicode":b'{"x":"\\ud800"}',
                     "decimal":b'{"x":1.0}',"exponent":b'{"x":1e0}',"overflow":b'{"x":9007199254740992}',"over-limit":b' '*MAX_REQUEST+b'{}'}
            output=dict(raw=base64(invalid[parts[2]]))
        elif len(parts)>2 and parts[2]=="mutate":
            target=output["challenge"] if kind=="challenge" else output
            if kind=="lease":
                header,claims,_=output["token"].split("."); header=parse_object(unbase64(header,len(header)*3//4)); target=parse_object(unbase64(claims,len(claims)*3//4))
            field=parts[3]
            if field=="extra": target[field]=1
            elif field=="missing": del target[next(iter(sorted(target)))]
            else: target[field]=_changed(target[field])
            if kind=="lease": output=dict(token=_raw_token(target,header))
        elif len(parts)>2 and parts[2]=="kdf":
            field=parts[3]; output["kdf"][field]=1 if field=="extra" else _changed(output["kdf"][field])
        elif len(parts)>2 and parts[2]=="header":
            head,payload,_=output["token"].split("."); header=parse_object(unbase64(head,len(head)*3//4)); claims=parse_object(unbase64(payload,len(payload)*3//4)); field=parts[3]
            if field=="duplicate":
                raw=encode_large(header); raw=raw[:-1]+b',"alg":"ES256"}'; prefix=base64(raw)+"."+payload; output=dict(token=prefix+"."+base64(keys.sign(key("lease")["private"],prefix.encode())))
            elif field=="expiry-policy":
                if "exp" in claims: del claims["exp"]
                else: claims["exp"]=NOW+86400
                output=dict(token=_raw_token(claims,header))
            else:
                name,value={"alg-none":("alg","none"),"alg-hs256":("alg","HS256"),"typ":("typ","JWT"),"kid":("kid","unknown"),"jku":("jku",ISSUER),"jwk":("jwk",key("author")["public"]),"crit":("crit",["x"])}[field]
                header[name]=value; output=dict(token=_raw_token(claims,header))
        elif len(parts)>2 and parts[2]=="scenario" and parts[3]=="reordered-body": output["raw"]=base64(b'{"expectedRevision":1,"idempotencyKey":"fixture"}')
        row=dict(rule,output=output,digest=digest(output),deterministic=kind in ("encoding","kdf","hkdf","signature","clock","model")); rows.append(row)
    return document("cases",rows)


def _expect_equal(left,right):
    if left!=right: raise ProtocolError()


def _execute(case):
    parts=case["id"].split("."); kind,name=parts[:2]; output=case["output"]
    if kind in ("content","password","recovery","recipient"):
        context=slot(name,name) if kind=="content" else slot(name+"-protection",name) if kind=="password" else slot("recovery") if kind=="recovery" else slot("record","record")
        if len(parts)>2 and parts[2]=="binding":
            context=Context(OTHER if parts[3]=="owner" else context.owner_id,context.resource_kind,OTHER if parts[3]=="resource" else context.resource_id,2 if parts[3]=="epoch" else 1,[])
        if kind=="content": _expect_equal(b'public fixture plaintext',open_envelope(ROOT,context,"cerberus-content-v1",output))
        elif kind=="password": _expect_equal(bundle(name),protection.unwrap(PASSWORD,output,context,membership(name),key("unlock-"+name)["public"]))
        elif kind=="recovery":
            value=recovery_bundle.unwrap(SECRET,output,context,1,key("recovery-1")["public"],membership("account"),key("unlock-account")["public"])
            _expect_equal(bundle("account"),value["protectionBundle"])
        else: _expect_equal(ROOT,recipient.open_recipient(output,context,"00000000-0000-0000-0000-000000000007",1,IDENTITY,key("recipient")["private"],ClientTrust(ACCOUNT,key("recipient")["public"],key("author")["public"],1)))
    elif kind=="challenge":
        now=NOW+60 if parts[-1]=="expiry" else NOW-1 if parts[-1]=="future" else NOW
        verifier=key("lease" if parts[-1]=="wrong-key" else role(name))["public"]
        challenge.verify(output["challenge"],binding(name),unbase64(output["raw"],len(output["raw"])*3//4),verifier,unbase64(output["proof"],64),now)
    elif kind=="lease": lease.verify(output["token"],authority(name=="enabled"),LeaseTrust(ISSUER,key("lease")["public"],1),NOW)
    elif kind=="encoding" and name=="invalid": parse_object(unbase64(output["raw"],len(output["raw"])*3//4))
    else: _expect_equal(_base(kind+"."+name),output)


def consume(document_in):
    if document_in.get("classification")!="public-test-fixtures": raise ProtocolError()
    rules={row["id"]:row for row in read(FIXTURES/"case-manifest.json")["cases"]}; rows=[]
    if len(document_in["cases"])!=len(rules) or len({c["id"] for c in document_in["cases"]})!=len(rules): raise ProtocolError()
    for case in document_in["cases"]:
        rule=rules[case["id"]]
        if case["kind"]!=rule["kind"] or case["expect"]!=rule["expect"] or case["digest"]!=digest(case["output"]): raise ProtocolError()
        observed="success"
        try: _execute(case)
        except ProtocolError as error:
            if error.code!="invalid_protocol": raise
            observed="invalid_protocol"
        rows.append(dict(id=case["id"],status="pass" if observed==rule["expect"] else "fail",digest=case["digest"]))
    return document("results",rows)


def _clock(name):
    wall=[NOW]; monotonic=[100]; clock=OfflineClock(lambda:wall[0],lambda:monotonic[0]); value=lease.claims(authority(),NOW,True,None); clock.renew(value,True)
    wall[0]+=10; monotonic[0]+=10; before=clock.effective_now()
    if name=="elapsed": return dict(effectiveNow=before)
    if name=="wall-rollback": wall[0]-=1
    elif name=="monotonic-rollback": monotonic[0]-=1
    else:
        clock.restart()
        if name=="import": lease.verify(lease.sign(value,key("lease")["private"]),authority(),LeaseTrust(ISSUER,key("lease")["public"],1),NOW+10)
    try: clock.effective_now()
    except ProtocolError: return dict(effectiveNow=before,blocked=True)
    raise ProtocolError()


def _model(name):
    # Public opaque valid metadata, never decrypted by the server reference model.
    envelope=dict(keyEpoch=1,keySalt=base64(bytes(32)),nonce=base64(bytes(12)),ciphertext="AA",tag=base64(bytes(16)))
    password=dict(envelope,format=protection.FORMAT,kdf=dict(algorithm="argon2id-v1.3",memoryKiB=65536,iterations=3,parallelism=4,salt=base64(bytes(16))))
    recovery=dict(envelope,format=recovery_bundle.FORMAT,generation=1,proofKeyFingerprint=keys.thumbprint(key("recovery-1")["public"]))
    state=dict(accountId=ACCOUNT,identityId=IDENTITY,scopeKind="account",scopeId=ACCOUNT,keyEpoch=1,protectionRevision=1,generation=1,revocationGeneration=1,
               unlockVerifier=key("unlock-account")["public"],recoveryVerifier=key("recovery-1")["public"],passwordWrapper=password,recoveryWrapper=recovery)
    model=RecoveryModel(state,lambda:NOW); password=copy.deepcopy(password); recovery=copy.deepcopy(recovery); password["keyEpoch"]=2; recovery.update(keyEpoch=2,generation=2,proofKeyFingerprint=keys.thumbprint(key("recovery-2")["public"]))
    operation="refresh-recovery" if name=="refresh" else "recover"
    raw=encode(dict(operation=operation,idempotencyKey="fixture",expectedRevision=1,passwordWrapper=password,recoveryWrapper=recovery,newRecoveryVerifier=key("recovery-2")["public"]))
    issued=challenge.issue(binding(operation),raw,NOW); model.add_challenge(issued); proof=challenge.sign(issued,key(role(operation))["private"])
    if name=="rollback":
        before=model.snapshot()
        try: model.execute(IDENTITY,True,raw,issued["challengeId"],proof,True,True)
        except ProtocolError: _expect_equal(before,model.snapshot()); return dict(rolledBack=True)
        raise ProtocolError()
    outcome=model.execute(IDENTITY,True,raw,issued["challengeId"],proof,True,False)
    if name=="retry":
        after=model.snapshot(); _expect_equal(outcome,model.execute(IDENTITY,True,raw,issued["challengeId"],proof,True,False)); _expect_equal(after,model.snapshot())
    return outcome


def self_test():
    from cryptography.hazmat.primitives.kdf.argon2 import Argon2id
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.hazmat.primitives import hpke
    from cryptography.hazmat.bindings._rust import openssl as native
    vectors=read(FIXTURES/"known-answers.json"); rows=[]
    v=vectors["hkdf"]; _expect_equal(v["okm"],primitives.hkdf(bytes.fromhex(v["ikm"]),bytes.fromhex(v["salt"]),bytes.fromhex(v["info"]),v["length"]).hex())
    for v in vectors["gcm"]["cases"]:
        _expect_equal(v["CT"]+v["Tag"],primitives.gcm_seal(bytes.fromhex(v["Key"]),bytes.fromhex(v["IV"]),bytes.fromhex(v["AAD"]),bytes.fromhex(v["PT"])).hex())
    v=vectors["ecdsa"]; _expect_equal(v["signature"].lower(),keys.sign(key("author")["private"],v["message"].encode()).hex())
    v=vectors["thumbprint"]; _expect_equal(v["value"],base64(primitives.sha256(v["canonical"].encode())))
    v=vectors["argon2"]; _expect_equal(v["tag"],Argon2id(salt=bytes.fromhex(v["salt"]),length=32,iterations=3,lanes=4,memory_cost=32,secret=bytes.fromhex(v["secret"]),ad=bytes.fromhex(v["ad"])).derive(bytes.fromhex(v["password"])).hex())
    v=vectors["hpke"]; case=v["encryptions"][0]; suite=hpke.Suite(hpke.KEM.P256,hpke.KDF.HKDF_SHA256,hpke.AEAD.AES_256_GCM)
    _expect_equal(bytes.fromhex(case["pt"]),native.hpke._decrypt_with_aad(suite,bytes.fromhex(v["enc"]+case["ct"]),ec.derive_private_key(int(v["skRm"],16),ec.SECP256R1()),info=bytes.fromhex(v["info"]),aad=bytes.fromhex(case["aad"])))
    return document("results",[dict(id=name,status="pass") for name in ("hkdf","gcm","ecdsa","thumbprint","argon2","hpke")])
