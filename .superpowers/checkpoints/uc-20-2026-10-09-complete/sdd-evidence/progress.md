# SDD ledger — plan: docs/superpowers/plans/2026-10-09-uc-20-delete-record.md

UC20 issue21; branch feature/uc-20-delete-record; freshbase1713397fea3deb4ae9d30119e493997eb7943514 verifiedUC19DonePR80; fresh baseline3589/3589zero failure-skip98.3line91.5branch exactmergedtree; no unchangedbaseline rerun. PrimaryfourSHAunchanged. No AGENTS; no nativeworktree tools, authorized gitworktreefallbackfromprimary; priorUC19retained separate.

Gate1: freshissue21OPEN/Todo and currentmergedspec main+AF01..05/FRRC05/BR08/09/20/commonentities/typedlinks/trash/authmatrix/workflow/testing/tech/ops loaded; architectural alternatives/bindingdesign and3-taskplan selfreviewed before product; everyflow mapped Data/Command/actualHTTP and requirementexists. Existing allissuesauthorization replaces design/plan/stage/remote menus.

Pre-flight Tasks1→2: RecordTrashRequest(Actor,AccessVerifier,RecordId,ExpectedRevision),RecordTrashDetails(RecordId,TrashOperationId,Revision,ServerSequence,DeletedAt,PurgeAt),IRecordTrashStore.TrashAsync(request,ct)->VaultResult<RecordTrashDetails> exactmatch.

Pre-flight Tasks1→3: RecordAssociationSnapshot(FolderId,ProfileIds,CollectionIds) restrictedtypedpayload/currentDB fixtures; HTTPtests inspectactualtypedoperation/entry/queue, no publicsnapshot.

Pre-flight Tasks2→3: DeleteRecordCommand.SetContext(actor,access,recordId)/JsonRequiredExpectedRevision/validator/handler/DeleteRecordOutputexact6fields toCommandMediator/DELETEroute; no interfaceconflicts.

Ruling: Purpose-built current-scope ownertrash transaction using existing generic typedtrash tables, no permission-framework refactor or separate readthenwrite — currentauthority/revision/restorablelinks must commit together — cost if wrong: targeted SQL duplication and lock maintenance complexity.

Ruling: Current native-visible foreign RO/RW returns403 before revision; hidden404 first; validate allcontributingnativeevidence without foreignresource locks before denial — recipients have no delete right, current snapshot safely linearizes a no-write denial — cost if wrong: corrupt relevant native route can503 despite never permitting delete.

Ruling: Owned trash does not parse/decrypt its opaque ciphertext and preserves it/clientEditedAt/owner exactly — deletion does not replace or return content, allowing deletion of damaged ciphertext — cost if wrong: retained corrupt ciphertext still prevents successful authenticated client restore or complete rotation until separately repaired/purged.

Ruling: Snapshot allstored direct typedlinks including inactive, detachfolder/removeProfileRecord+CollectionRecord atomically — restricted historical publicID snapshot supports later validrestore while activeassociations disappear — cost if wrong: UC51 must revalidate/reconcile snapshot and typedentry before retrash, never blindly revive permissions.

Ruling: Bump active directly affected ownedprofile/collection/immediatefolder structuralrevision/sequence once; preserveopaquecontent/clienttime/keymetadata; no inheritedancestor/recipientprofile bumps — directmembership removal is structural like UC16creation — cost if wrong: later durablevisibility sync must also compute dynamicdescendant/removal effects.

Ruling: Keep selectedprofile handles valid and nonnull — recordtrash does notdelete profile or change its policy/scope — cost if wrong: callers must reload target after success; no resource-level handle revocation implied.

Ruling: OwnaccountNoKey→sessionUpdate→ALLrelatedownprofilesincludingselectionGUIDNoKey→owncollectionsGUIDNoKey→immediatefolderNoKey→recordUpdate→existingtypedassociationrowsUpdate — parents permit FKKeyShare, targetexcludesnewrefs and associationrowsstabilize removals; compatible with ownedaccount serialization and crossownercollectionbeforecontent order — cost if wrong: actual FK/rotation/recipient/association concurrency tests must reveal cycles; future writers must preserve protocol.

Ruling: Newunheldparent/association409 afterfreshscope and no lateearlierlocks; test newinserts BEFOREtargetlock, then finalcompleteancestry/currentauthority guards — targetUPDATE excludes later FKrefinserts; earliernewrefs require reload — cost if wrong: extra retry/recursion and statement-snapshot linearization.

Ruling: Existingtypedtrashentry with active ownedrecord is conflictingstate409; retryalreadytrashed404 withoutdeadlineextension — do not overwrite restorationhistory or create secondoperation — cost if wrong: futureUC51 must consume/reconcile typedentry before retrash.

Ruling: Queue trash/{operationGUID} using existing failclosed executor; actualrestore/expiry handler remainsUC51/53 beforeRelease — no physicalpurge is authorized by this recoverable endpoint; retainedrecord stays in complete rotationinventory — cost if wrong: missing later handler delays physicalpurge and must block release, never falselycomplete work.

Ruling: Retain conservative globalterminalGUID exclusions until typedrepair BEFORE ANY physicalpurge and nevernull selectedProfileId — known cross-kind collision safety prerequisite retained — cost if wrong: sameGUID acrossresource kinds can falselyhide untilrepair.

Ruling: Independent security/protocol and actualclient approvals remain development-onlydeferred under explicituser instruction; ordinaryreview/native/fullsuite/CI andreleasegates remain — cost if wrong: latentqualification defects await mandatory release approval.

Tasks1in_progress/2pending/3pending. ONEfreshordinarywholebranchreview atfinish; no implementationagents/per-taskreviewers. Task3implementation/testcommit beforeglobalreview/integration; UC20Done onlyafterall9DoD.

Task1 RED observed /tmp/cerberus-uc20-data-red.log exit1 CS0246 missing RecordTrashStore/RecordTrashDetails; firstbehavioralData matrix written before product. Minimal Domain+purpose-built store now written, focusedGREEN next. Shared private targeted currentauthority/nativeevidence SQL copied from reviewedUC19, no existingproductrefactor/schemachanges.

Task1 firstimplementation90/90 focusedPASS zero failure/skip (data-green-1.log), no corrections needed. Added remaining planned controlled actualcreate/association/recipient/rotation races, currentlink-removal/cycles/selection/overlappingnative/foreignlock/EXPLAINANALYZE cases beforewholeData completion; do not claim the additional cases failed missingtypes ininitialRED. NativeOSV153/corpus322fourdirections/36helpers/23Python/spec107FR55UC29BR freshPASS.

ExpandedTask1 107/107PASS (data-green-2.log) inclactual controlledrecipient/create/association/rotation/race/foreignlock andEXPLAINANALYZEonecandidate/twoancestors vs80unrelateddeep records. Added lastplanned rotation-first/currentownerhandle and parentsequence exhaustion/regression-afterrecordwrite/terminalbump/unsafeparentseq cases; no product corrections so far.

Task1 finalexpanded114/114PASS zero fail-skip (data-green-3.log), still no product corrections. Last2 planned selectednativeRO/RW recipient get/list removal with unchangedrecipientprofile/handle/link added; unfiltered wholeData now testsALL116newcases +1188baseline =1304expected. WholeData output pending, no Task1complete/commit claim yet.

Task1 wholeData1304/1304PASS zero failures/skips (task1-data.log), all116newcases presentincllastselectednativeRO/RW viewremoval/currenthandle/foreignprofileunchanged; no product correction needed after missingtypesRED. Allbrief expected outputs compared; current-scope/native/owner-only/lifecycle/retainedassociations/activeparentbumps/atomicrollback/actualstores/finalancestry/FKlocks/onecandidatequeryplan verified. Task1commit then taskdone next.
Task 1: complete (commits 5eb11c8..19badc4, tests: python3 /tmp/cerberus-audit-test-log.py /tmp/cerberus-uc20-task1-data.log 1304 → Verified fresh completed test log: 1304 passed, zero failures/skips (/tmp/cerberus-uc20-task1-data.log))

Task2 RED observed command-red.log exit1 CS0246 missing DeleteRecordCommand/Handler before5commandproductfiles;74case strictcontext/revision/result/error/JSON/cancellation matrix written first. Minimalcommand nowimplemented; focused then wholeCommand next.

Task2 firstimplementation74/74 focusedPASS (command-green.log) and wholeCommand641/641PASS zero failure-skip (task2-command.log), no correctionsneeded. Exact1body/6metadata/context/hash/ct/positiveboundaries/nativeproofcontextnotaccepted/safeallowlist/null/errorwithdata/unknown/corruptresult/time/expiry/JSON tested. AllbriefExpected compared; commit/taskdone next.
Task 2: complete (commits 19badc4..f3b1dba, tests: python3 /tmp/cerberus-audit-test-log.py /tmp/cerberus-uc20-task2-command.log 641 → Verified fresh completed test log: 641 passed, zero failures/skips (/tmp/cerberus-uc20-task2-command.log))

Task3 actualHTTP99 RED observed http-red.log exit1:93missingDELETE405failures/6existingidentity-common413-cancellationPASS. Matrix written beforeDELETE/DI withactualUC16create andnativeUC15Master/PerProfile/currentgrant fixtures. Addedminimalroute/context/statuses+3DIregistrations; focusedGREEN next.

Task3 focusedHTTP99/99PASS zero failure-skip (http-green-1.log) after observed missingrouteRED; no product corrections. API/operators/changelog/READMEUC20DoneM03twelve updated; OpenAPI/NuGet/full3878 next.

Task3 fresh unfiltered sixfamily3878PASS zero fail-skip; all6productionassemblies>=90line, merged98.3%line/91.7%branch. Freshlockedrestore/NuGetclean/nativeOSV153/corpus322/36nativehelpers/23Python/spec107FR55UC29BR/OpenAPIgenerated+drift+shape/diff PASS. Task3 allExpected compared, no correction afterrouteRED; commit/taskdone then mandatory globalreview/PR/CI/integrationDoD still pending.
Task 3: complete (commits f3b1dba..67042c4, tests: python3 /tmp/cerberus-audit-test-log.py /tmp/cerberus-uc20-coverage.log 3878 → Verified fresh completed test log: 3878 passed, zero failures/skips (/tmp/cerberus-uc20-coverage.log))

Gate2/3 actual main+AF01..05 verified against committed code/tests: /tmp/cerberus-uc20-flow-verification.log. No missingflow or danglingrequirement. One fresh ordinary finalreview /root/uc20_final_review running; independent formal approvalsdevelopment-onlydeferred. Freshdevelopstill1713397 and exact3requiredCI/norequiredhumanapproval confirmed.

Final ordinaryreview67042c4 complete /tmp/cerberus-uc20-final-review.md:0Critical/0Importantblocking;2existingOpenAPIminors; all12initialrulings+5Focuschecked. Regradedactualeffects: inherited/future Importantreleaselimitations explicitlypreserved below; none newlyintroducedUC20runtimeblocker. No blockingfixpass/re-review needed.

Final: Ruling: Actual restoration and association re-admission remains UC51 beforeRelease — current development endpoint retains historical typed associations, but a restore endpoint must revalidate links/authority and consume or reconcile the old entry — cost if wrong: users cannot recover through the API yet, and unsafe future readmission could revive unauthorized links; Important release limitation.
Final: Ruling: Actual erasure at720hours remains UC53 beforeRelease — existing executor fails closed/retries unavailable typedhandler and never marks fakepurgecomplete; scheduling is not completed erasure — cost if wrong: ciphertext can remain beyond deadline until handler ships; Important release limitation.
Final: Ruling: Resource-kind-aware terminal identity repair remains mandatory BEFORE ANY physicalpurge — inherited globalGUIDexclusions/uniqueness conservatively deny and this branch adds no purge; nevernullselectedProfileId — cost if wrong: cross-kind sameGUID can falselyhide live content; Important availability limitation until typedrepair.
Final: Ruling: Durable offline removal/descendant notifications remain future synchronization integration — online currentget/list exclude trash now, while later sync must publish dynamicvisibilityremovals — cost if wrong: released offlineclients could retain stalevisiblecontent; Important release limitation for offline product.
Final: Ruling: Independent cryptographic/protocol and actual external-client approvals remain explicitly development-onlydeferred — ordinaryreview/nativecorpus/actualHTTP do not replace those qualifications and releasegates staymandatory — cost if wrong: latent security or interoperability defects await required release approval.
Final: Ruling: Future sharing/foldermove/lifecycle/restore writers require their own integration tests with this orderedlock/currentauthority protocol — currentactualwriters reviewed and racescovered, unwrittenwriters cannot be certified now; BEFORE UC35 implicitrecipient-accountFK audit staysmandatory — cost if wrong: future deadlock/scope failures could be Important or securityCritical.
Final: Ruling: Repair/authenticated recovery of alreadydamagedciphertext remains a separate operation — deletion preservesexistingopaquebytes and doesnotclaimtocure corruption; intactmetadata and unchangedcontentguard required — cost if wrong: damagedretainedcontent can still prevent authenticated client recovery and complete rotation until repair/purge.
Final: Ruling: Do not broaden this atomic endpoint into generalized permission/authority framework refactor — purposebuiltSQL currentlymatches reviewedauthority and observablecorrectness; broadrefactor needs separate behavioralreview — cost if wrong: SQLduplication/maintenance complexity can cause futuredrift.
Final: minor (deferred): Shared OpenAPI lacks required header/requestBody and guaranteed six-outputfield annotations (openapi.json2352/2359/5059); inputexpectedRevisionISrequired; runtime strictness tested. Correctsharedgeneratorbeforerelease; generatedclients may treatinputs/metadataasoptional.
Final: minor (deferred): Shared OpenAPI advertises ProblemDetails content for actualempty413 (openapi.json2499, HTTPtest96); commonlimittestrejectswithoutbody/mutation. Correctsharedgeneratorbeforerelease; generatedparsermayfailonemptyresponse.

UC20 integrated COMPLETE: PR81 normalmatchheadsquash a59310da31ea48e3561d8bd2507f6ca62b5ae44d;latestexactheadbranch-policy/test/dockerSUCCESS;issue21Closed/9DoDchecked/projectDone/remotebranchabsent;freshdevelopSHA+tree==testedHEAD;4primarySHAunchanged. Ownledger/review/package/logs/coverageSummary archived beforeonlyownscratchcleanup. UC21next.
