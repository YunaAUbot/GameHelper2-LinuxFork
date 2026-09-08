#!/usr/bin/env python3
"""Prepare externally; apply only an explicitly reviewed, unchanged runtime plan."""
import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import stat

spec = importlib.util.spec_from_file_location('worker', pathlib.Path(__file__).with_name('git-plugin-worker.py'))
w = importlib.util.module_from_spec(spec)
spec.loader.exec_module(w)
HOST_FILES = ('GameHelper.dll', 'GameHelper.pdb', 'Localization/en-US.json',
              'scripts/git-plugin-worker.py', 'README-LINUX.md')
STATE_FILES = ()  # Legacy registries and credential configuration are never migration targets.


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def prepare(runtime, payload, destination):
    for path in (runtime, payload, destination):
        w.safe_tree(path)
    if destination.exists() or runtime == destination or runtime in destination.parents:
        raise w.InstallError('Use a new destination outside the runtime.')
    before = w.file_hashes(runtime)
    if set(w.file_hashes(payload)) != set(HOST_FILES):
        raise w.InstallError('Payload must contain exactly the five reviewed host files.')
    destination.mkdir(parents=True)
    shutil.copytree(runtime, destination/'backup')
    shutil.copytree(payload, destination/'payload')
    if before != w.file_hashes(destination/'backup') or before != w.file_hashes(runtime):
        raise w.InstallError('Runtime changed during capture; discard preparation and retry after normal closure.')
    plan = dict(schema=1, runtime=str(runtime), before=before,
                payload=w.file_hashes(destination/'payload'), files=list(HOST_FILES+STATE_FILES))
    w.atomic(destination/'plan.json', plan)
    return digest(destination/'plan.json')


def apply(destination, review_sha, closed, reconciled, rollback=False):
    w.safe_tree(destination)
    if not closed or not reconciled or digest(destination/'plan.json') != review_sha:
        raise w.InstallError('Exact plan review, normal helper/worker closure and personal-state reconciliation are required.')
    plan = json.loads((destination/'plan.json').read_text())
    runtime = pathlib.Path(plan['runtime']); w.safe_tree(runtime)
    if plan['files'] != list(HOST_FILES+STATE_FILES) or set(plan['payload']) != set(plan['files']):
        raise w.InstallError('Invalid migration allowlist.')
    if w.file_hashes(destination/'backup') != plan['before'] or w.file_hashes(destination/'payload') != plan['payload']:
        raise w.InstallError('Backup or reviewed payload changed.')
    after = dict(plan['before']); after.update(plan['payload'])
    # One exclusive journal identifies the inode we created, not merely a name
    # ending in .migration-tmp. Keep it outside the protected runtime inventory.
    journal = destination/'copy-in-progress.json'
    owned = None
    if journal.exists():
        if not rollback:
            raise w.InstallError('Interrupted copy requires explicit rollback.')
        if not stat.S_ISREG(journal.stat().st_mode) or journal.stat().st_nlink != 1:
            raise w.InstallError('Unexpected migration journal.')
        record = json.loads(journal.read_text())
        if (set(record) != {'review', 'relative', 'folder', 'device', 'inode'}
                or record['review'] != review_sha or record['relative'] not in plan['files']
                or record['folder'] not in ('payload', 'backup')):
            raise w.InstallError('Unexpected migration journal.')
        relative = record['relative']
        temporary = runtime/(relative+'.migration-tmp')
        if temporary.exists():
            info = temporary.stat()
            source = destination/record['folder']/relative
            if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1
                    or (info.st_dev, info.st_ino) != (record['device'], record['inode'])
                    or not source.is_file()
                    or not source.read_bytes().startswith(temporary.read_bytes())):
                raise w.InstallError('Unexpected migration temporary content or identity.')
            owned = str(temporary.relative_to(runtime))
    if any(relative+'.migration-tmp' in plan['before'] for relative in plan['files']):
        raise w.InstallError('Reserved migration temporary path belongs to the captured runtime.')
    current = w.file_hashes(runtime)
    if owned is not None:
        del current[owned]
    if rollback:
        unchanged = {k: v for k, v in current.items() if k not in plan['files']}
        expected = {k: v for k, v in plan['before'].items() if k not in plan['files']}
        valid = unchanged == expected and all(current.get(k) in (plan['before'].get(k), after.get(k)) for k in plan['files'])
    else:
        valid = current == plan['before']
    if not valid:
        raise w.InstallError('Runtime changed; prepare a fresh plan. Never restore over newer personal state.')
    # Validate all protected state before deleting even an owned temporary file.
    if owned is not None:
        (runtime/owned).unlink()
    if journal.exists():
        journal.unlink()

    def copy_files(folder, hashes):
        for relative in plan['files']:
            target = runtime/relative
            if relative in hashes:
                target.parent.mkdir(exist_ok=True)
                temporary = target.with_name(target.name+'.migration-tmp')
                if temporary.exists() or temporary.is_symlink():
                    raise w.InstallError('Migration temporary path already exists.')
                # Exclusive creation never truncates a pre-existing user file.
                # A crash before the journal is complete fails closed; no copy
                # starts until its ownership record has been flushed.
                with temporary.open('xb') as stream:
                    info = os.fstat(stream.fileno())
                    with journal.open('x') as log:
                        json.dump(dict(review=review_sha, relative=relative, folder=folder.name,
                                       device=info.st_dev, inode=info.st_ino), log)
                        log.flush()
                        os.fsync(log.fileno())
                shutil.copy2(folder/relative, temporary)
                if target.exists(): temporary.chmod(target.stat().st_mode & 0o7777)
                temporary.replace(target)
                journal.unlink()
            elif target.exists():
                target.unlink()
    # No plugin path is ever a write target. An interruption leaves the complete
    # backup available; do not start the helper until the reviewed host set agrees.
    try:
        copy_files(destination/('backup' if rollback else 'payload'), plan['before'] if rollback else plan['payload'])
        if w.file_hashes(runtime) != (plan['before'] if rollback else after):
            raise w.InstallError('Post-apply inventory mismatch; retained backup requires review.')
    except BaseException:
        if not rollback:
            apply(destination, review_sha, closed, reconciled, rollback=True)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=('prepare', 'apply', 'rollback'))
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('--runtime', type=pathlib.Path)
    parser.add_argument('--payload', type=pathlib.Path)
    parser.add_argument('--review-sha')
    parser.add_argument('--helper-worker-closed', action='store_true')
    parser.add_argument('--personal-state-reconciled', action='store_true')
    args = parser.parse_args()
    if args.mode == 'prepare':
        if not args.runtime or not args.payload: parser.error('prepare requires runtime and payload')
        print(prepare(args.runtime.absolute(), args.payload.absolute(), args.destination.absolute()))
    else:
        apply(args.destination.absolute(), args.review_sha, args.helper_worker_closed,
              args.personal_state_reconciled, args.mode == 'rollback')

if __name__ == '__main__':
    main()
