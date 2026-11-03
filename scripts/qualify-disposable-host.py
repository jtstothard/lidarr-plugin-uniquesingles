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
RUN = ROOT / '_temp' / ('issue49-' + uuid.uuid4().hex)
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
    # The HTTP listener is ready before ApplicationStarted handlers finish restoring
    # plugin tasks. Wait for that lifecycle outcome, not an arbitrary startup sleep.
    for _ in range(120):
        results['tasksAfterRestart'] = api('system/task')
        if any(x['taskName'] == 'UniqueSinglesScan' for x in results['tasksAfterRestart']):
            break
        time.sleep(.25)
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
    # ── Issue #49: native album-import route and fault-injection stages (second artist) ──
    # A dedicated synthetic artist isolates these stages from the issue-42 library above.
    LIBRARY2 = RUN / 'synthetic-library/Artist2'
    WRAPPERS = RUN / 'wrappers'

    def seed_second_artist(db):
        LIBRARY2.mkdir(parents=True)
        insert(db, 'ArtistMetadata', Id=2, ForeignArtistId='synthetic-issue49', Name='Synthetic Issue49', Status=0, Images='[]')
        insert(db, 'Artists', Id=2, CleanName='syntheticissue49', Path=str(LIBRARY2), Monitored=1, MetadataProfileId=1, ArtistMetadataId=2, MonitorNewItems=0, QualityProfileId=1, Tags='[]', Added='2026-11-03 00:00:00')
        insert(db, 'Albums', Id=500, ForeignAlbumId='synthetic-album-500', Title='Retained', CleanTitle='retained', Images='[]', Monitored=1, AlbumType='Album', ArtistMetadataId=2, AnyReleaseOk=1, OldForeignAlbumIds='[]', SecondaryTypes='[]')
        insert(db, 'AlbumReleases', Id=500, ForeignReleaseId='synthetic-release-500', AlbumId=500, Title='Retained', Status='Official', Duration=900000, Monitored=1, OldForeignReleaseIds='[]', TrackCount=1, Media='[]')
        path = LIBRARY2 / 'Retained/Song.flac'
        path.parent.mkdir()
        shutil.copyfile(FIXTURES / 'same-tags.flac', path)
        insert(db, 'TrackFiles', Id=501, AlbumId=500, Quality='{"quality":6,"revision":{"version":1,"real":0,"isRepack":false}}', Size=path.stat().st_size, DateAdded='2026-11-03 00:00:00', Modified='2026-11-03 00:00:00', Path=str(path))
        insert(db, 'Tracks', Id=500, ForeignTrackId='synthetic-track-500', Title='Song', Explicit=0, TrackFileId=501, Duration=900000, MediumNumber=1, AbsoluteTrackNumber=1, TrackNumber='1', ForeignRecordingId='issue49-source', AlbumReleaseId=500, ArtistMetadataId=2)

    def seed_candidate(db, album_id, fixture='original.flac'):
        folder = LIBRARY2 / f'Single{album_id}'
        path = folder / 'Song.flac'
        folder.mkdir()
        shutil.copyfile(FIXTURES / fixture, path)
        insert(db, 'Albums', Id=album_id, ForeignAlbumId=f'synthetic-album-{album_id}', Title=f'Candidate {album_id}', CleanTitle=f'candidate{album_id}', Images='[]', Monitored=1, AlbumType='Single', ArtistMetadataId=2, AnyReleaseOk=1, OldForeignAlbumIds='[]', SecondaryTypes='[]')
        insert(db, 'AlbumReleases', Id=album_id, ForeignReleaseId=f'synthetic-release-{album_id}', AlbumId=album_id, Title=f'Candidate {album_id}', Status='Official', Duration=100000, Monitored=1, OldForeignReleaseIds='[]', TrackCount=1, Media='[]')
        # Wide id gaps: Lidarr auto-assigns TrackFiles ids from max(Id)+1 during imports,
        # so contiguous explicit ids would collide with the rows those imports create.
        insert(db, 'TrackFiles', Id=album_id * 1000, AlbumId=album_id, Quality='{"quality":6,"revision":{"version":1,"real":0,"isRepack":false}}', Size=path.stat().st_size, DateAdded='2026-11-03 00:00:00', Modified='2026-11-03 00:00:00', Path=str(path))
        insert(db, 'Tracks', Id=album_id, ForeignTrackId=f'synthetic-track-{album_id}', Title='Song', Explicit=0, TrackFileId=album_id * 1000, Duration=100000, MediumNumber=1, AbsoluteTrackNumber=1, TrackNumber='1', ForeignRecordingId=f'issue49-candidate-{album_id}', AlbumReleaseId=album_id, ArtistMetadataId=2)

    def drop_candidate(db, album_id):
        # Fault-preserved candidates are removed before the next stage so each fault stage
        # is evaluated in isolation; their files stay on disk as run evidence.
        db.execute(f'DELETE FROM Tracks WHERE AlbumReleaseId={album_id}')
        db.execute(f'DELETE FROM TrackFiles WHERE AlbumId={album_id}')
        db.execute(f'DELETE FROM AlbumReleases WHERE AlbumId={album_id}')
        db.execute(f'DELETE FROM Albums WHERE Id={album_id}')

    def write_wrapper(name, body):
        WRAPPERS.mkdir(exist_ok=True)
        wrapper = WRAPPERS / name
        wrapper.write_text('#!/bin/sh\n' + body)
        wrapper.chmod(0o755)
        return wrapper

    def host_log():
        return '\n'.join(Path(x.name).read_text() for x in logs)

    def wait_for_log(fragment, timeout=20):
        for _ in range(timeout * 10):
            text = host_log()
            if fragment in text:
                return text
            time.sleep(.1)
        return host_log()

    def scan_second_artist(label, expected):
        return command(label, {'name': 'UniqueSinglesScan', 'artistId': 2}, expected)

    stop()
    sql(lambda db: (seed_second_artist(db), seed_candidate(db, 600)))
    start()

    # Native album-import route: a real import into the retained ALBUM dispatches the
    # notification's artist-cleanup route (parity with the proven single-import route).
    provider('notification', {'releaseTypesToCheck': 'Album', 'exactAudioCleanup': True, 'exactAudioDecoderPath': shutil.which('ffmpeg')})
    incoming2 = RUN / 'incoming2/Song.flac'
    incoming2.parent.mkdir()
    shutil.copyfile(FIXTURES / 'original.flac', incoming2)
    command('album-import-cleanup', {'name': 'ManualImport', 'importMode': 'copy', 'replaceExistingFiles': True, 'files': [{'path': str(incoming2), 'artistId': 2, 'albumId': 500, 'albumReleaseId': 500, 'trackIds': [500], 'quality': {'quality': {'id': 6, 'name': 'FLAC'}, 'revision': {'version': 1, 'real': 0}}, 'disableReleaseSwitching': True}]})
    text = wait_for_log('route=artist-cleanup')
    assert any('route=artist-cleanup' in line and 'cleaned=1' in line for line in text.splitlines()), text[-4000:]
    assert not api('album/600')['monitored']
    assert api('album/500')['monitored']
    assert not (LIBRARY2 / 'Single600/Song.flac').exists()
    retained = [Path(x['path']) for x in api('trackfile?artistId=2') if x['albumId'] == 500]
    assert retained and all(p.exists() for p in retained)
    # The delete-time guard revalidated content identity as part of each destructive call.
    assert 'candidateReleaseId=600' in text and 'decision=revalidated action=delete' in text

    # Mid-analysis candidate mutation: the decoder wrapper replaces the candidate file
    # after the verifier's identity read but before/during its decode; the post-decode
    # recheck must reject the run's own evidence and preserve the release.
    stop()
    sql(lambda db: seed_candidate(db, 601))
    start()
    candidate_path = LIBRARY2 / 'Single601/Song.flac'
    mutate_candidate = write_wrapper('mutate-candidate', f"cp '{FIXTURES / 'late-sample.flac'}' '{candidate_path}'\nexec ffmpeg \"$@\"")
    provider('notification', {'exactAudioDecoderPath': str(mutate_candidate)})
    scan_second_artist('mid-analysis-mutation-preserves-release', '1 flagged for review')
    assert api('album/601')['monitored'] and candidate_path.exists()
    assert 'identity-changed-during-analysis' in host_log()
    provider('notification', {'exactAudioDecoderPath': shutil.which('ffmpeg')})

    # Decode failure mid-analysis: a corrupt candidate FLAC fails the native decoder.
    stop()
    sql(lambda db: (drop_candidate(db, 601), seed_candidate(db, 602, fixture='corrupt.flac')))
    start()
    scan_second_artist('decode-failure-mid-analysis-preserves-release', '1 flagged for review')
    assert api('album/602')['monitored'] and (LIBRARY2 / 'Single602/Song.flac').exists()
    assert 'decode-unavailable-or-invalid' in host_log()

    # Decoder crash: the decoder process is killed by a signal during analysis.
    stop()
    sql(lambda db: (drop_candidate(db, 602), seed_candidate(db, 603)))
    start()
    crash_decoder = write_wrapper('crash-decoder', 'kill -9 $$\n')
    provider('notification', {'exactAudioDecoderPath': str(crash_decoder)})
    scan_second_artist('decoder-crash-preserves-release', '1 flagged for review')
    assert api('album/603')['monitored'] and (LIBRARY2 / 'Single603/Song.flac').exists()
    assert 'status=Unavailable' in host_log()
    provider('notification', {'exactAudioDecoderPath': shutil.which('ffmpeg')})

    # Unmonitor failure injection: a disposable-database trigger makes the album unmonitor
    # write fail; files must be preserved and the failure accounted, then recovery cleans.
    stop()
    def block_unmonitor(db):
        drop_candidate(db, 603)
        seed_candidate(db, 604)
        db.execute("CREATE TRIGGER issue49_block_unmonitor BEFORE UPDATE ON Albums WHEN NEW.Monitored = 0 BEGIN SELECT RAISE(ABORT, 'issue49 injected unmonitor failure'); END;")
    sql(block_unmonitor)
    start()
    scan_second_artist('unmonitor-failure-injection-preserves-files', '1 unmonitor failures')
    assert api('album/604')['monitored'] and (LIBRARY2 / 'Single604/Song.flac').exists()
    assert 'unmonitor-failed-delete-skipped' in host_log()
    stop()
    sql(lambda db: db.execute('DROP TRIGGER issue49_block_unmonitor'))
    start()
    scan_second_artist('unmonitor-failure-recovery-cleans', '1 singles cleaned')
    assert not api('album/604')['monitored'] and not (LIBRARY2 / 'Single604/Song.flac').exists()

    # Delete failure injection: the candidate directory is made read-only so the native
    # deletion fails; the release stays unmonitored with its file preserved and accounted.
    stop()
    sql(lambda db: seed_candidate(db, 605))
    start()
    guarded_dir = LIBRARY2 / 'Single605'
    guarded_dir.chmod(0o555)
    try:
        scan_second_artist('delete-failure-injection-files-preserved', '1 delete failures')
    finally:
        guarded_dir.chmod(0o755)
    assert not api('album/605')['monitored']
    assert (guarded_dir / 'Song.flac').exists()
    assert 'reason=delete-failed' in host_log()

    # Shutdown cancellation: SIGTERM mid-decode cancels in-flight analysis through the
    # dispatcher lifecycle; the release must abort to keep and clean on the next scan.
    stop()
    sql(lambda db: seed_candidate(db, 606))
    start()
    marker = WRAPPERS / 'decoder-started'
    slow_decoder = write_wrapper('slow-decoder', f"printf started > '{marker}'\nsleep 4\nexec ffmpeg \"$@\"")
    provider('notification', {'exactAudioDecoderPath': str(slow_decoder)})
    value = api('command', {'name': 'UniqueSinglesScan', 'artistId': 2})
    for _ in range(200):
        if marker.exists():
            break
        time.sleep(.1)
    assert marker.exists(), 'slow decoder never started; cannot inject mid-analysis shutdown'
    stop()  # SIGTERM during the in-flight decode
    cancelled_text = host_log()
    tokens = [token for token in ('cancelled-or-time-budget', 'detail=cancelled', 'dispatcher-shutdown') if token in cancelled_text]
    # Lidarr's shutdown does not wait for the in-flight plugin command, so its cancellation
    # evidence line is not guaranteed to be drained before process exit; record it as
    # best-effort. The safety outcome is asserted directly and deterministically instead:
    # no destructive action may have started for the release being analyzed.
    results['stages']['shutdown-cancellation'] = {'commandId': value['id'], 'decoderStarted': marker.exists(), 'cancellationTokensObserved': tokens}
    assert (LIBRARY2 / 'Single606/Song.flac').exists(), 'cancelled analysis must not delete'
    assert not any('singleId=606' in line and ('unmonitor success' in line or 'delete success' in line) for line in cancelled_text.splitlines()), 'destructive action during cancelled analysis'
    assert not any('candidateReleaseId=606' in line and 'decision=revalidated' in line for line in cancelled_text.splitlines()), 'revalidated approval during cancelled analysis'
    start()
    provider('notification', {'exactAudioDecoderPath': shutil.which('ffmpeg')})
    assert api('album/606')['monitored'], 'cancelled analysis must abort to keep'
    scan_second_artist('cancellation-recovery-cleans', '1 singles cleaned')
    assert not api('album/606')['monitored'] and not (LIBRARY2 / 'Single606/Song.flac').exists()

    results['completed'] = True
finally:
    stop()
    for output in logs:
        output.close()
    (RUN / 'results.json').write_text(json.dumps(results, indent=2) + '\n')
    print('Evidence:', RUN, flush=True)
