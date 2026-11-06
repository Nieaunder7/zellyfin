const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const modulePromise = import('data:text/javascript;base64,' + Buffer.from(fs.readFileSync(path.join(
    process.env.JELLYFIN_WEB_SOURCE, 'src/scripts/playerFileActions.js'), 'utf8')).toString('base64'));
const source = { Id: 'a', Path: '/video/a.mp4', Protocol: 'File' };
const item = { Id: 'a', CanDelete: true, MediaType: 'Video', LocationType: 'FileSystem', MediaSources: [source] };
function fixture(overrides = {}) {
    const calls = [];
    const deps = {
        confirm: async () => { calls.push('confirm'); return true; },
        refresh: async () => { calls.push('refresh'); return item; },
        isCurrent: () => true,
        getQueue: async () => [item, { Id: 'b' }, item, { Id: 'c' }],
        getIndex: () => 0,
        stop: async () => { calls.push('stop'); },
        remove: async () => { calls.push('delete'); },
        play: async items => { calls.push(['play', items.map(i => i.Id)]); },
        finish: async () => { calls.push('finish'); },
        ...overrides
    };
    return { deps, calls };
}
test('approval stops before deleting and resumes remaining queue without deleted duplicates', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    const { deps, calls } = fixture();
    assert.equal(await createPlayerFileDeletion(deps)(item, source), 'deleted');
    assert.deepEqual(calls, ['confirm', 'refresh', 'stop', 'delete', ['play', ['b', 'c']]]);
});
test('cancellation leaves playback and files untouched', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    const { deps, calls } = fixture({ confirm: async () => false });
    assert.equal(await createPlayerFileDeletion(deps)(item, source), 'cancelled');
    assert.deepEqual(calls, []);
});
test('delete failure never starts next item', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    const { deps, calls } = fixture({ remove: async () => { throw Error('denied'); } });
    await assert.rejects(createPlayerFileDeletion(deps)(item, source), /denied/);
    assert.deepEqual(calls, ['confirm', 'refresh', 'stop']);
});
test('stop failure never deletes', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    const { deps, calls } = fixture({ stop: async () => { throw Error('stop failed'); } });
    await assert.rejects(createPlayerFileDeletion(deps)(item, source), /stop failed/);
    assert.deepEqual(calls, ['confirm', 'refresh']);
});
test('last queued item closes player only after deletion', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    const { deps, calls } = fixture({ getQueue: async () => [item] });
    await createPlayerFileDeletion(deps)(item, source);
    assert.deepEqual(calls, ['confirm', 'refresh', 'stop', 'delete', 'finish']);
});
test('changed playback or revoked permissions prevent stop and deletion', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    for (const change of [{ isCurrent: () => false }, { refresh: async () => ({ ...item, CanDelete: false }) },
        { getQueue: async () => [{ Id: 'other' }] }]) {
        const { deps, calls } = fixture(change);
        await assert.rejects(createPlayerFileDeletion(deps)(item, source));
        assert.ok(!calls.includes('stop') && !calls.includes('delete'));
    }
});
test('duplicate clicks produce only one confirmation and deletion', async () => {
    const { createPlayerFileDeletion } = await modulePromise;
    let approve;
    const { deps, calls } = fixture({ confirm: () => new Promise(resolve => { approve = resolve; }) });
    const remove = createPlayerFileDeletion(deps);
    const first = remove(item, source);
    assert.equal(await remove(item, source), 'busy');
    approve(true);
    await first;
    assert.equal(calls.filter(c => c === 'delete').length, 1);
});
test('alternate versions, folders, multipart and remote files are not single-file deletion targets', async () => {
    const { canDeletePlayingFile } = await modulePromise;
    assert.equal(canDeletePlayingFile(item, source), true);
    for (const change of [{ IsFolder: true }, { PartCount: 2 }, { MediaSources: [source, source] },
        { MediaSourceCount: 2 }, { CanDelete: false }, { VideoType: 'Dvd' }]) {
        assert.equal(canDeletePlayingFile({ ...item, ...change }, source), false);
    }
    assert.equal(canDeletePlayingFile(item, { ...source, IsRemote: true }), false);
    assert.equal(canDeletePlayingFile(item, { ...source, Path: '/another.mp4' }), false);
});
test('size formatting distinguishes unknown and zero and uses binary units', async () => {
    const { formatFileSize } = await modulePromise;
    assert.equal(formatFileSize(undefined), null);
    assert.equal(formatFileSize(-1), null);
    assert.equal(formatFileSize(0), '0 B');
    assert.equal(formatFileSize(1024 ** 3), '1 GiB');
});
