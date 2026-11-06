const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE');
const source = fs.readFileSync(path.join(web, 'src/scripts/sceneSearchPosition.js'), 'utf8');
const functions = import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

test('scene search shows hours and position within a long video', async () => {
    const { formatSceneTime, getSceneSearchPosition } = await functions;
    const duration = 3 * 60 * 60 * 10000000;
    const sample = 2 * 60 * 60 * 10000000 + 5 * 60 * 10000000 + 6 * 10000000;
    assert.equal(formatSceneTime(sample), '2:05:06');
    assert.equal(formatSceneTime(duration), '3:00:00');
    assert.equal(getSceneSearchPosition(sample, duration), 69.5);
    assert.equal(getSceneSearchPosition(duration + 1, duration), 100);
    assert.equal(getSceneSearchPosition(sample, 0), null);
});
