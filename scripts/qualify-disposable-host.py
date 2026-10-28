#!/usr/bin/env python3
"""Run local-only acceptance on a NEW pinned Lidarr installation and synthetic DB.

Never accepts an existing data/library directory. Requires a Release build and the
verified archive prepared by prepare-lidarr.py. Leaves evidence under _temp only.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import socket
import sqlite3
import subprocess
import tarfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parent.parent
spec = importlib.util.spec_from_file_location('prepare', ROOT / 'scripts/prepare-lidarr.py')
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)
RID = {'Darwin': 'osx', 'Linux': 'linux'}[platform.system()] + '-core-' + {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64'}[platform.machine()]
ARCHIVE = ROOT / '_temp/lidarr' / f'Lidarr.develop.{prepare.VERSION}.{RID}.tar.gz'
prepare.verify_archive(ARCHIVE, prepare.SHA256[RID])
RUN = ROOT / '_temp' / ('issue42-' + uuid.uuid4().hex)
RUN.mkdir()
with tarfile.open(ARCHIVE) as archive:
    archive.extractall(RUN / 'host', filter='data')
HOST = RUN / 'host/Lidarr/Lidarr'
if platform.system() == 'Darwin':
    subprocess.run(['codesign', '--force', '--sign', '-', str(HOST)], check=True)
DATA = RUN / 'data'
DATA.mkdir()
PLUGIN = ROOT / '_temp/bin/Release/UniqueSingles/Lidarr.Plugin.UniqueSingles.dll'
installed = DATA / 'plugins/jtstothard/UniqueSingles'
installed.mkdir(parents=True)
shutil.copy2(PLUGIN, installed)
with socket.socket() as sock:
    sock.bind(('127.0.0.1', 0))
    PORT = sock.getsockname()[1]
KEY = uuid.uuid4().hex
(DATA / 'config.xml').write_text(f'<Config><BindAddress>127.0.0.1</BindAddress><Port>{PORT}</Port><EnableSsl>False</EnableSsl><LaunchBrowser>False</LaunchBrowser><AuthenticationMethod>External</AuthenticationMethod><AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired><ApiKey>{KEY}</ApiKey><LogLevel>debug</LogLevel><AnalyticsEnabled>False</AnalyticsEnabled><UpdateAutomatically>False</UpdateAutomatically></Config>')
process = None
logs = []
results = {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), 'pluginSha256': hashlib.sha256(PLUGIN.read_bytes()).hexdigest(), 'archiveSha256': prepare.SHA256[RID], 'rid': RID, 'run': str(RUN), 'stages': {}}

decoder = shutil.which('ffmpeg')
if decoder:
    results['decoder'] = {'path': str(Path(decoder).resolve()), 'sha256': hashlib.sha256(Path(decoder).read_bytes()).hexdigest(), 'version': subprocess.check_output([decoder, '-version'], text=True).splitlines()[0]}
results['platform'] = platform.platform()

def api(path, body=None, method=None):
    request = urllib.request.Request(f'http://127.0.0.1:{PORT}/api/v1/{path}', data=None if body is None else json.dumps(body).encode(), headers={'X-Api-Key': KEY, 'Content-Type': 'application/json'}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            content = response.read()
            return json.loads(content) if content else None
    except urllib.error.HTTPError as error:
        raise RuntimeError(error.read().decode()) from error

def start():
    global process
    output = (RUN / f'host-{len(logs)}.log').open('w')
    logs.append(output)
    process = subprocess.Popen([str(HOST), '-nobrowser', '-data=' + str(DATA)], stdout=output, stderr=subprocess.STDOUT)
    for _ in range(120):
        if process.poll() is not None:
            raise RuntimeError(f'Host exited {process.returncode}; see {output.name}')
        try:
            api('system/status')
            return
        except (OSError, RuntimeError):
            time.sleep(.25)
    raise TimeoutError('Disposable host startup')

def stop():
    global process
    if process is not None:
        process.terminate()
        process.wait(timeout=30)
        process = None
    for output in logs:
        output.flush()

def sql(action):
    assert process is None, 'Only seed the stopped disposable database'
    with sqlite3.connect(DATA / 'lidarr.db') as db:
        return action(db)

def insert(db, table, **fields):
    db.execute(f'INSERT INTO "{table}" ({",".join(fields)}) VALUES ({",".join("?" for _ in fields)})', list(fields.values()))

LIBRARY = RUN / 'synthetic-library/Artist'
FIXTURES = ROOT / 'tests/UniqueSingles.Test/AudioFixtures'
CANDIDATE = LIBRARY / 'Candidate/Song.flac'
SOURCE = LIBRARY / 'Retained/Song.flac'

def seed(db):
    LIBRARY.mkdir(parents=True)
    insert(db, 'ArtistMetadata', Id=1, ForeignArtistId='synthetic-issue42', Name='Synthetic Issue42', Status=0, Images='[]')
    insert(db, 'Artists', Id=1, CleanName='syntheticissue42', Path=str(LIBRARY), Monitored=1, MetadataProfileId=1, ArtistMetadataId=1, MonitorNewItems=0, QualityProfileId=1, Tags='[]', Added='2026-10-28 00:00:00')
    for i, title, typ, fixture, duration in [(100, 'Retained', 'Album', 'same-tags.flac', 900000), (200, 'Candidate', 'Single', 'original.flac', 100000)]:
        path = LIBRARY / title / 'Song.flac'
        path.parent.mkdir()
        shutil.copyfile(FIXTURES / fixture, path)
        insert(db, 'Albums', Id=i, ForeignAlbumId=f'synthetic-album-{i}', Title=title, CleanTitle=title.lower(), Images='[]', Monitored=1, AlbumType=typ, ArtistMetadataId=1, AnyReleaseOk=1, OldForeignAlbumIds='[]', SecondaryTypes='[]')
        insert(db, 'AlbumReleases', Id=i, ForeignReleaseId=f'synthetic-release-{i}', AlbumId=i, Title=title, Status='Official', Duration=duration, Monitored=1, OldForeignReleaseIds='[]', TrackCount=1, Media='[]')
        insert(db, 'TrackFiles', Id=i, AlbumId=i, Quality='{"quality":6,"revision":{"version":1,"real":0,"isRepack":false}}', Size=path.stat().st_size, DateAdded='2026-10-28 00:00:00', Modified='2026-10-28 00:00:00', Path=str(path))
        insert(db, 'Tracks', Id=i, ForeignTrackId=f'synthetic-track-{i}', Title='Song', Explicit=0, TrackFileId=i, Duration=duration, MediumNumber=1, AbsoluteTrackNumber=1, TrackNumber='1', ForeignRecordingId=f'synthetic-recording-{i}', AlbumReleaseId=i, ArtistMetadataId=1)
    # Update the host-created singleton definition; do not add a duplicate provider.
    settings = {'exactAudioAnalysis': True, 'exactAudioCleanup': False, 'exactAudioDecoderPath': shutil.which('ffmpeg') or '/absent-issue42-decoder', 'releaseTypesToCheck': 'Album', 'durationToleranceMs': 1234, 'scanIntervalMinutes': 10080, 'tier3Action': 0}
    db.execute('UPDATE Metadata SET Settings=? WHERE Implementation=?', (json.dumps(settings), 'UniqueSinglesScanTask'))

def command(label, body, expected=None):
    value = api('command', body)
    for _ in range(240):
        value = api('command/' + str(value['id']))
        if value['status'] in ('completed', 'failed', 'aborted'):
            break
        time.sleep(.1)
    results['stages'][label] = value
    assert value['status'] == 'completed', value
    if expected:
        assert expected in value.get('message', ''), value
    print(label, value.get('message', value['status']), flush=True)
    return value

def scan(label, expected):
    return command(label, {'name': 'UniqueSinglesScan'}, expected)

def provider(kind, changes, enable=None):
    value = next(x for x in api(kind) if x['implementation'] in ('UniqueSinglesScanTask', 'UniqueSinglesNotification'))
    for field in value['fields']:
        if field['name'] in changes:
            field['value'] = changes[field['name']]
    if enable is not None:
        value['enable'] = enable
    return api(kind + '/' + str(value['id']), value, 'PUT')

def intact():
    assert CANDIDATE.exists() and SOURCE.exists()
    assert api('album/200')['monitored'] and api('album/100')['monitored']

try:
    start()
    results['host'] = api('system/status')
    stop()
    sql(seed)
    start()
    assert not any(x['taskName'] == 'UniqueSinglesScan' for x in api('system/task'))
    scan('metadata-report', '1 exact duplicates (report only)')
    intact()
    scan('warm-cache-report', '1 exact duplicates (report only)')
    intact()
    provider('metadata', {'exactAudioCleanup': True})
    shutil.copyfile(FIXTURES / 'late-sample.flac', SOURCE)
    scan('changed-source-rejects-cached-evidence', '1 flagged for review')
    intact()
    shutil.copyfile(FIXTURES / 'same-tags.flac', SOURCE)
    provider('metadata', {'exactAudioDecoderPath': '/absent-issue42-decoder'})
    scan('decoder-absent', '1 flagged for review')
    intact()
    # Connect must override the now deliberately unusable Metadata decoder.
    notification = next(x for x in api('notification/schema') if x['implementation'] == 'UniqueSinglesNotification')
    notification.update(name='Synthetic Issue42 Connect', onReleaseImport=True)
    changes = {'exactAudioAnalysis': True, 'exactAudioCleanup': False, 'exactAudioDecoderPath': shutil.which('ffmpeg'), 'durationToleranceMs': 2345, 'releaseTypesToCheck': 'Album'}
    for field in notification['fields']:
        if field['name'] in changes:
            field['value'] = changes[field['name']]
    api('notification', notification)
    scan('connect-precedence-report', '1 exact duplicates (report only)')
    intact()
    provider('notification', {'releaseTypesToCheck': 'Single', 'exactAudioCleanup': True})
    scan('self-source-is-excluded', '0 singles cleaned')
    intact()
    provider('notification', {'releaseTypesToCheck': 'Album', 'exactAudioCleanup': False})
    # Native retained-source eligibility must still protect the candidate.
    source_album = api('album/100')
    source_album['monitored'] = False
    api('album/100', source_album, 'PUT')
    scan('unmonitored-source-keeps-candidate', '0 singles cleaned')
    assert api('album/200')['monitored'] and CANDIDATE.exists()
    source_album['monitored'] = True
    api('album/100', source_album, 'PUT')
    # One exclusive B-side suppresses the release-level review count.
    stop()
    sql(lambda db: insert(db, 'Tracks', Id=201, ForeignTrackId='synthetic-exclusive', Title='Exclusive B-side', Explicit=0, TrackFileId=0, Duration=9000, MediumNumber=1, AbsoluteTrackNumber=2, TrackNumber='2', ForeignRecordingId='exclusive', AlbumReleaseId=200, ArtistMetadataId=1))
    start()
    scan('exclusive-whole-release-keep', '0 flagged for review')
    intact()
    stop()
    sql(lambda db: db.execute('DELETE FROM Tracks WHERE Id=201'))
    start()
    api('rootfolder', {'path': str(LIBRARY.parent), 'name': 'Issue42 Synthetic Root', 'defaultQualityProfileId': 1, 'defaultMetadataProfileId': 1, 'defaultMonitorOption': 'all', 'defaultNewItemMonitorOption': 'all'})
    # Exercise the real manual import command and Connect's AlbumDownload event.
    incoming = RUN / 'incoming/Song.flac'
    incoming.parent.mkdir()
    shutil.copyfile(FIXTURES / 'original.flac', incoming)
    command('manual-import-report', {'name': 'ManualImport', 'importMode': 'copy', 'replaceExistingFiles': True, 'files': [{'path': str(incoming), 'artistId': 1, 'albumId': 200, 'albumReleaseId': 200, 'trackIds': [200], 'quality': {'quality': {'id': 6, 'name': 'FLAC'}, 'revision': {'version': 1, 'real': 0}}, 'disableReleaseSwitching': True}]})
    # Import events are dispatched asynchronously; bounded wait for their evidence.
    for _ in range(100):
        text = '\n'.join(Path(x.name).read_text() for x in logs)
        if 'UniqueSingles import result:' in text:
            break
        time.sleep(.1)
    results['importLogObserved'] = 'UniqueSingles import result:' in text
    assert results['importLogObserved'], 'Command completion alone does not prove import; inspect host logs'
    assert 'UniqueSingles import exact-audio summary: reportOnlyExactDuplicates=1' in text
    assert api('album/200')['monitored'] and SOURCE.exists()
    candidate_paths = [Path(x['path']) for x in api('trackfile?artistId=1') if x['albumId'] == 200]
    assert candidate_paths and all(p.exists() for p in candidate_paths)
    provider('notification', {'exactAudioCleanup': True})
    scan('connect-explicit-cleanup', '1 singles cleaned')
    assert not api('album/200')['monitored']
    assert api('album/100')['monitored'] and SOURCE.exists()
    assert not CANDIDATE.exists() and all(not p.exists() for p in candidate_paths)
    # Repeat the actual import with explicit cleanup enabled, checking the event
    # outcome rather than trusting the dispatcher's generic completion message.
    candidate_album = api('album/200')
    candidate_album['monitored'] = True
    api('album/200', candidate_album, 'PUT')
    command('manual-import-opt-in', {'name': 'ManualImport', 'importMode': 'copy', 'replaceExistingFiles': True, 'files': [{'path': str(incoming), 'artistId': 1, 'albumId': 200, 'albumReleaseId': 200, 'trackIds': [200], 'quality': {'quality': {'id': 6, 'name': 'FLAC'}, 'revision': {'version': 1, 'real': 0}}, 'disableReleaseSwitching': True}]})
    for _ in range(100):
        text = '\n'.join(Path(x.name).read_text() for x in logs)
        if any('UniqueSingles import result:' in line and 'cleaned=1' in line for line in text.splitlines()):
            break
        time.sleep(.1)
    assert any('UniqueSingles import result:' in line and 'cleaned=1' in line for line in text.splitlines())
    assert not api('album/200')['monitored'] and SOURCE.exists() and api('album/100')['monitored']
    assert not [x for x in api('trackfile?artistId=1') if x['albumId'] == 200]
    provider('metadata', {'exactAudioDecoderPath': shutil.which('ffmpeg')}, enable=True)
    # Finish a dispatched command before restart so the schedule must not become due.
    scan('scheduled-opt-in', '0 singles cleaned')
    before = api('system/task')
    results['tasksBeforeRestart'] = before
    stop()
    start()
    results['tasksAfterRestart'] = api('system/task')
    scheduled_before = next(x for x in before if x['taskName'] == 'UniqueSinglesScan')
    scheduled_after = next(x for x in results['tasksAfterRestart'] if x['taskName'] == 'UniqueSinglesScan')
    assert scheduled_before['interval'] == scheduled_after['interval'] == 10080
    assert scheduled_before['lastExecution'] == scheduled_after['lastExecution']
    assert scheduled_before['nextExecution'] == scheduled_after['nextExecution']
    results['stages']['restart-scan'] = scan('restart-scan', '0 singles cleaned')
    provider('metadata', {}, enable=False)
    assert not any(x['taskName'] == 'UniqueSinglesScan' for x in api('system/task'))
    stop()
    def survivors(db):
        for i, fixture, duration in [(300, 'original.flac', 100000), (400, 'same-tags.flac', 900000)]:
            path = LIBRARY / str(i) / 'Survivor.flac'
            path.parent.mkdir()
            shutil.copyfile(FIXTURES / fixture, path)
            insert(db, 'Albums', Id=i, ForeignAlbumId=f'synthetic-album-{i}', Title=f'Survivor {i}', CleanTitle=f'survivor{i}', Images='[]', Monitored=1, AlbumType='Single', ArtistMetadataId=1, AnyReleaseOk=1, OldForeignAlbumIds='[]', SecondaryTypes='[]')
            insert(db, 'AlbumReleases', Id=i, ForeignReleaseId=f'synthetic-release-{i}', AlbumId=i, Title=f'Survivor {i}', Status='Official', Duration=duration, Monitored=1, OldForeignReleaseIds='[]', TrackCount=1, Media='[]')
            insert(db, 'TrackFiles', Id=i, AlbumId=i, Quality='{"quality":6,"revision":{"version":1,"real":0,"isRepack":false}}', Size=path.stat().st_size, DateAdded='2026-10-28 00:00:00', Modified='2026-10-28 00:00:00', Path=str(path))
            insert(db, 'Tracks', Id=i, ForeignTrackId=f'synthetic-track-{i}', Title='Survivor', Explicit=0, TrackFileId=i, Duration=duration, MediumNumber=1, AbsoluteTrackNumber=1, TrackNumber='1', ForeignRecordingId=f'synthetic-recording-{i}', AlbumReleaseId=i, ArtistMetadataId=1)
    sql(survivors)
    start()
    provider('notification', {'releaseTypesToCheck': 'Single', 'exactAudioCleanup': True})
    scan('equivalent-singles-retain-survivor', '1 singles cleaned')
    assert sum(api(f'album/{i}')['monitored'] for i in (300, 400)) == 1
    assert sum((LIBRARY / str(i) / 'Survivor.flac').exists() for i in (300, 400)) == 1
    results['completed'] = True
finally:
    stop()
    for output in logs:
        output.close()
    (RUN / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
    print('Evidence:', RUN, flush=True)
