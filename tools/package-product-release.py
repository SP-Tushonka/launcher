"""Build or assemble a separate product release. Never launches a product or tests.

Assembly consumes exact independently supplied launcher/runtime inventories. The
runtime implementation is external; it is never borrowed from research folders.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
GUI_REQUIRED = {'SPT.Launcher.exe', 'SPT.Launcher.dll', 'SPT.Launcher.deps.json',
                'SPT.Launcher.runtimeconfig.json', 'SPTarkov.Core.dll'}
RUNTIME_REQUIRED = {'eft-runtime.exe', 'eft-runtime.dll', 'eft-runtime.deps.json',
                    'eft-runtime.runtimeconfig.json', 'product_daemon.py',
                    'product_adapter.py', 'runtime-config.json'}
FORBIDDEN = {'tests', 'test-results', 'private-docs', 'analysis', 'evidence', 'research', 'patches',
             'user', 'profiles', 'controller', '__pycache__', '.git', 'obj', 'bin'}


def physical(path):
    path = Path(path)
    for part in (path, *path.parents):
        if part.is_symlink() or (part.exists() and getattr(part.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError('Physical paths required; reparse point refused')


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result: raise ValueError('Duplicate JSON field')
        result[key] = value
    return result


def read(path):
    physical(path)
    return json.loads(Path(path).read_text(encoding='utf-8-sig'), object_pairs_hook=unique)


def child(root, name):
    if not isinstance(name, str) or not name or '\\' in name or ':' in name:
        raise ValueError('Canonical relative payload path required')
    relative = PurePosixPath(name)
    if relative.is_absolute() or relative.as_posix() != name or any(
        part in ('.', '..') or part.casefold() in FORBIDDEN or part.rstrip(' .') != part or any(ord(c)<32 for c in part) or
        part.split('.')[0].casefold() in {'con', 'prn', 'aux', 'nul', *('com'+str(i) for i in range(1,10)), *('lpt'+str(i) for i in range(1,10))}
        for part in relative.parts):
        raise ValueError('Unsafe or private payload path')
    target = root.joinpath(*relative.parts)
    physical(target)
    if not target.resolve().is_relative_to(root.resolve()): raise ValueError('Payload path escapes input')
    return target


def inventory(root):
    physical(root)
    result = {}
    for path in root.rglob('*'):
        physical(path)
        if path.is_file():
            result[path.relative_to(root).as_posix()] = {'sha256': sha(path), 'bytes': path.stat().st_size}
    return result


def verify_input(root, receipt_path, expected, required):
    root = root.absolute(); physical(root)
    if not re.fullmatch('[a-f0-9]{64}', expected) or sha(receipt_path) != expected:
        raise ValueError('Input manifest hash mismatch')
    receipt = read(receipt_path)
    rows = receipt.get('files')
    if not isinstance(rows, dict) or not required.issubset(rows): raise ValueError('Complete product dependency inventory required')
    seen = set()
    for name, row in rows.items():
        path = child(root, name)
        if name.casefold() in seen: raise ValueError('Case-aliased input path')
        seen.add(name.casefold())
        if not isinstance(row, dict) or type(row.get('bytes')) is not int or not re.fullmatch('[a-f0-9]{64}', row.get('sha256', '')):
            raise ValueError('Each product file needs exact SHA256 and byte size')
        if not path.is_file() or sha(path) != row['sha256'] or path.stat().st_size != row['bytes']:
            raise ValueError('Product input differs from supplied inventory: ' + name)
        if path.suffix.casefold() in ('.pdb', '.pyc', '.7z', '.zip') or path.name.casefold().startswith(('test_', 'check-', 'fixture_')):
            raise ValueError('Debug, transport, test, and checking harness files are not product payloads')
    if inventory(root) != rows: raise ValueError('Product input directory has missing/unlisted files')
    return rows


def fresh_output(path):
    path = path.absolute(); physical(path)
    if path.exists(): raise ValueError('Fresh output required; no existing release is deleted or overwritten')
    protected = ('Build', 'SPTushonka.Core', 'SPTushonka.Launcher', 'tools', 'tests', 'private-docs', '.git')
    if ROOT.is_relative_to(path) or any(path.is_relative_to(ROOT/name) for name in protected):
        raise ValueError('Output cannot replace source or enter stock Build/product source directories')
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def build(args):
    output = fresh_output(args.output)
    cache = args.package_cache.absolute(); physical(cache)
    if not cache.is_dir(): raise ValueError('Existing offline package cache required')
    # Configuration lives outside publish payload and is removed after building.
    with tempfile.TemporaryDirectory(prefix='product-build-input-', dir=output.parent) as directory:
        config = Path(directory) / 'nuget.config'
        config.write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
        project = ROOT / 'SPTushonka.Launcher/SPTushonka.Launcher.csproj'
        properties = ['-p:ProductRelease=true', '-p:ProductReleaseOutput='+output.as_posix(),
                      '-p:NuGetAudit=false', '-p:NuGetPackageRoot='+cache.as_posix().rstrip('/')+'/',
                      '-p:DebugType=None', '-p:DebugSymbols=false', '-p:GenerateDocumentationFile=false']
        environment = dict(os.environ, NUGET_PACKAGES=str(cache))
        for command in ([args.dotnet, 'restore', str(project), '--configfile', str(config), '-r', 'win-x64', *properties],
                        [args.dotnet, 'publish', str(project), '--no-restore', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', *properties]):
            subprocess.run(command, cwd=ROOT, env=environment, check=True)
    rows = inventory(output)
    if not GUI_REQUIRED.issubset(rows): raise ValueError('Published GUI closure is incomplete')
    receipt = output.with_name(output.name+'.manifest.json')
    with receipt.open('x', encoding='utf-8') as stream:
        json.dump({'schemaVersion': 1, 'kind': 'product-launcher', 'testsRun': False, 'files': rows}, stream, indent=2)
        stream.write('\n')
    print(json.dumps({'built': True, 'testsRun': False, 'manifest': str(receipt), 'manifestSha256': sha(receipt)}))


def package(args):
    output = fresh_output(args.output)
    gui = args.launcher_directory.absolute(); runtime = args.runtime_directory.absolute()
    if output.is_relative_to(gui) or output.is_relative_to(runtime) or gui.is_relative_to(output) or runtime.is_relative_to(output):
        raise ValueError('Release and input directories must be separate')
    gui_rows = verify_input(gui, args.launcher_manifest, args.launcher_manifest_sha, GUI_REQUIRED)
    runtime_rows = verify_input(runtime, args.runtime_manifest, args.runtime_manifest_sha, RUNTIME_REQUIRED)
    if any(name.startswith('runtime/') or name.casefold() in ('release-manifest.json', 'license.md', 'runtime-third-party.md') for name in gui_rows):
        raise ValueError('GUI payload cannot shadow release metadata or runtime')
    if {'daemon.py','adapter.py','play.py'} & set(runtime_rows):
        raise ValueError('Legacy research entrypoints are not product Runtime files')
    config_raw=(runtime/'runtime-config.json').read_bytes()
    if hashlib.sha256(config_raw).hexdigest()!=runtime_rows['runtime-config.json']['sha256']:
        raise ValueError('Runtime configuration changed before validation')
    config=json.loads(config_raw.decode('utf-8-sig'),object_pairs_hook=unique)
    if not isinstance(config, dict): raise ValueError('Product runtime configuration must be an object')
    text = json.dumps(config)
    roots = [root.absolute() for root in args.deployment_root]
    if args.deployment_config != bool(roots):
        raise ValueError('Machine-local mode requires --deployment-config and explicit --deployment-root values together')
    for root in roots:
        physical(root)
        if root == Path(root.anchor): raise ValueError('A deployment root must be narrower than a volume root')
    if not args.deployment_config and re.search(r'[A-Za-z]:[/\\]+do[/\\]+decompiler', text, re.I):
        raise ValueError('Research workspace configuration is not a portable release template')
    def validate_deployment(value):
        if isinstance(value, dict):
            for key, item in value.items():
                if re.search(r'queue|probe|debugger|experiment', key, re.I):
                    raise ValueError('Research harness configuration is not a product dependency')
                validate_deployment(item)
        elif isinstance(value, list):
            for item in value: validate_deployment(item)
        elif isinstance(value, str) and (Path(value).is_absolute() or re.match(r'^[A-Za-z]:[/\\]', value)):
            if args.deployment_config:
                path = Path(value).absolute(); physical(path)
                if not any(path.is_relative_to(root) for root in roots):
                    raise ValueError('Machine-local configuration path is outside selected deployment roots')
        elif isinstance(value, str) and re.fullmatch(r'[a-fA-F0-9]{24}', value):
            raise ValueError('Profile IDs must not enter product configuration')
    validate_deployment(config)
    def private_value(value):
        if isinstance(value, dict):
            return any((key.casefold() in ('token','password','secret','privatekey','profile','profiles','username','account','profileid','sessionid','authorization','cookie','credentials') and bool(item)) or private_value(item) for key,item in value.items())
        return isinstance(value, list) and any(private_value(item) for item in value)
    if private_value(config): raise ValueError('Credentials must not enter the product configuration')
    if sha(args.notice_file) != args.notice_sha: raise ValueError('Third-party notice pin differs')
    physical(args.notice_file)
    stage = Path(tempfile.mkdtemp(prefix='.product-release-', dir=output.parent)).absolute()
    # On any assembly failure leave the isolated stage for inspection. Never
    # delete an uncertain product tree or touch an existing deployed release.
    for source_root, rows, prefix in ((gui, gui_rows, ''), (runtime, runtime_rows, 'runtime/')):
        for name, row in rows.items():
            source = child(source_root, name)
            if sha(source) != row['sha256']: raise ValueError('Input changed during assembly')
            target = child(stage, prefix+name); target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            if sha(target) != row['sha256']: raise ValueError('Product copy verification failed')
    shutil.copyfile(ROOT / 'LICENSE.md', stage / 'LICENSE.md')
    shutil.copyfile(args.notice_file, stage / 'RUNTIME-THIRD-PARTY.md')
    if sha(stage/'RUNTIME-THIRD-PARTY.md')!=args.notice_sha:raise ValueError('Third-party notices changed during assembly')
    record = {'schemaVersion': 1, 'kind': 'product-release', 'files': inventory(stage),
              'inputs': {'launcher': args.launcher_manifest_sha, 'runtime': args.runtime_manifest_sha, 'thirdPartyNotices': args.notice_sha},
              'runtimeEntrypoint': 'runtime/eft-runtime.exe',
              'configurationKind': 'machine-local' if args.deployment_config else 'portable-template',
              'deploymentRoots': [str(root) for root in roots], 'testsRun': False, 'liveValidationClaimed': False}
    with (stage / 'release-manifest.json').open('x', encoding='utf-8') as stream:
        json.dump(record, stream, indent=2); stream.write('\n'); stream.flush(); os.fsync(stream.fileno())
    physical(output)
    if output.exists(): raise ValueError('Output appeared before publication; prepared product stage retained')
    os.rename(stage, output)
    print(json.dumps({'packaged': True, 'files': len(record['files'])+1, 'testsRun': False, 'output': str(output)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    builder = commands.add_parser('build')
    builder.add_argument('--dotnet', default='dotnet')
    builder.add_argument('--package-cache', type=Path, required=True)
    builder.add_argument('--output', type=Path, required=True)
    assembler = commands.add_parser('package')
    for name in ('launcher', 'runtime'):
        assembler.add_argument('--'+name+'-directory', type=Path, required=True)
        assembler.add_argument('--'+name+'-manifest', type=Path, required=True)
        assembler.add_argument('--'+name+'-manifest-sha', required=True)
    assembler.add_argument('--notice-file', type=Path, required=True)
    assembler.add_argument('--notice-sha', required=True)
    assembler.add_argument('--output', type=Path, required=True)
    assembler.add_argument('--deployment-config', action='store_true', help='Private machine-local product configuration, not a portable template')
    assembler.add_argument('--deployment-root', type=Path, action='append', default=[], help='Allowed installed product or preserved data root; repeat as needed')
    args = parser.parse_args()
    (build if args.command == 'build' else package)(args)


if __name__ == '__main__': main()
