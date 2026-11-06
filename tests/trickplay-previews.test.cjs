const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE');
const source = fs.readFileSync(path.join(web, 'src/scripts/trickplayPreviews.js'), 'utf8');
const modulePromise = import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

const apiClient = {
    accessToken: () => 'test-token',
    getUrl: (endpoint, options) => `${endpoint}?MediaSourceId=${options.MediaSourceId}`
};

test('preview frames select the nearest 25%, 50%, and 75% trickplay tiles', async () => {
    const { getTrickplayFrames } = await modulePromise;
    const item = {
        Id: 'video', MediaType: 'Video', RunTimeTicks: 4000000000,
        Trickplay: { source: { 320: {
            Width: 320, Height: 180, TileWidth: 4, TileHeight: 3,
            Interval: 10000, ThumbnailCount: 40
        } } }
    };
    const frames = getTrickplayFrames(item, apiClient);
    assert.deepEqual(frames.map(frame => frame.percent), [25, 50, 75]);
    assert.deepEqual(frames.map(frame => frame.time), ['1:40', '3:20', '5:00']);
    assert.deepEqual(frames.map(frame => frame.url.split('/').at(-1).split('?')[0]), ['0.jpg', '1.jpg', '2.jpg']);
    assert.equal(frames[0].backgroundSize, '400% 300%');
    assert.equal(frames[1].backgroundPosition, '0% 100%');
    assert.equal(frames[2].backgroundPosition, `${200 / 3}% 50%`);
});

test('missing or incomplete trickplay data produces no preview', async () => {
    const { getTrickplayFrames } = await modulePromise;
    assert.deepEqual(getTrickplayFrames({ MediaType: 'Video', Trickplay: {} }, apiClient), []);
    assert.deepEqual(getTrickplayFrames({ MediaType: 'Photo' }, apiClient), []);
});

test('completed scene analysis replaces three fixed frames with evenly spaced scene images', async () => {
    const { getScenePreviewFrames } = await modulePromise;
    let requests = 0;
    const client = {
        accessToken: () => 'test-token',
        getUrl: (endpoint, options) => `${endpoint}${options ? '?ApiKey=test-token' : ''}`,
        getJSON: async () => {
            requests++;
            return {
                status: 'complete',
                scenes: Array.from({ length: 24 }, (_, index) => ({
                    timeTicks: (index + 1) * 100000000,
                    imageUrl: `SceneAnalysis/Items/video/Frames/${index}.jpg`
                }))
            };
        }
    };
    const item = { Id: 'video', MediaType: 'Video', RunTimeTicks: 2500000000 };
    const frames = await getScenePreviewFrames(item, client, 6);
    assert.equal(frames.length, 6);
    assert.deepEqual(frames.map(frame => frame.percent), [4, 24, 40, 60, 76, 96]);
    assert.match(frames[0].url, /Frames\/0.jpg\?ApiKey=test-token/);
    assert.match(frames.at(-1).url, /Frames\/23.jpg\?ApiKey=test-token/);
    await getScenePreviewFrames(item, client, 12);
    assert.equal(requests, 1);
});

test('incomplete analysis retains the existing three-image preview', async () => {
    const { createStoryPreviewStrip } = await modulePromise;
    const { JSDOM } = require(path.join(web, 'node_modules/jsdom'));
    const dom = new JSDOM('');
    global.document = dom.window.document;
    const client = {
        accessToken: () => 'test-token',
        getUrl: endpoint => endpoint,
        getJSON: async () => ({ status: 'running', scenes: [{ timeTicks: 100000000 }] })
    };
    const strip = await createStoryPreviewStrip({ Id: 'pending', MediaType: 'Video', RunTimeTicks: 1000000000 }, client);
    assert.equal(strip.querySelectorAll('.storyPreviewFrame').length, 3);
});
