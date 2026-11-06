const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE');
const { JSDOM } = require(path.join(web, 'node_modules/jsdom'));

test('video card previews cycle automatically while visible', async () => {
    const dom = new JSDOM('<body></body>', { pretendToBeVisual: true });
    global.window = dom.window;
    global.document = dom.window.document;

    const timers = new Map();
    let nextTimer = 1;
    dom.window.setTimeout = callback => {
        const id = nextTimer++;
        timers.set(id, () => {
            timers.delete(id);
            callback();
        });
        return id;
    };
    dom.window.clearTimeout = id => timers.delete(id);

    const intersections = [];
    class TestIntersectionObserver {
        constructor(callback) {
            this.callback = callback;
            this.targets = [];
            intersections.push(this);
        }
        observe(target) { this.targets.push(target); }
        unobserve(target) { this.targets = this.targets.filter(item => item !== target); }
        enter(target) { this.callback([{ target, isIntersecting: true }]); }
    }
    dom.window.IntersectionObserver = TestIntersectionObserver;

    let requests = 0;
    global.__previewClient = {
        getCurrentUserId: () => 'user',
        getItem: () => { throw new Error('The card item was already provided'); }
    };
    global.__createPreviewStrip = async () => {
        requests++;
        const strip = document.createElement('div');
        strip.className = 'storyPreviewStrip';
        for (let i = 0; i < 6; i++) {
            const frame = document.createElement('figure');
            frame.className = 'storyPreviewFrame';
            strip.append(frame);
        }
        return strip;
    };

    const file = fs.readFileSync(path.join(web, 'src/scripts/cardStoryCarousel.js'), 'utf8');
    const source = file
        .replace("import { ServerConnections } from 'lib/jellyfin-apiclient';",
            'const ServerConnections = { getApiClient: () => globalThis.__previewClient };')
        .replace("import { createStoryPreviewStrip } from 'scripts/trickplayPreviews';",
            'const createStoryPreviewStrip = (...args) => globalThis.__createPreviewStrip(...args);');
    const { installCardStoryCarousel, rememberCardStoryItem } = await import(
        'data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

    rememberCardStoryItem({ Id: 'video', MediaType: 'Video', RunTimeTicks: 1000000000 }, 'server');
    installCardStoryCarousel();
    const card = document.createElement('div');
    card.className = 'card';
    card.dataset.storypreview = 'true';
    card.dataset.mediatype = 'Video';
    card.dataset.serverid = 'server';
    card.dataset.id = 'video';
    card.getBoundingClientRect = () => ({ top: 0, bottom: 200, left: 0, right: 200 });
    const image = document.createElement('div');
    image.className = 'cardImageContainer';
    card.append(image);
    document.body.append(card);
    await new Promise(resolve => setImmediate(resolve));

    assert.equal(intersections.length, 2);
    assert.deepEqual(intersections[0].targets, [card]);
    assert.deepEqual(intersections[1].targets, [card]);
    assert.equal(requests, 0);
    intersections[0].enter(card);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(requests, 1);
    assert.equal(card.querySelectorAll('.storyPreviewInline').length, 0);
    assert.equal(image.querySelectorAll('.storyPreviewCarousel .storyPreviewFrame').length, 6);
    assert.equal(card.classList.contains('storyPreviewPlaying'), false);

    intersections[1].enter(card);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(card.classList.contains('storyPreviewPlaying'), true);
    assert.equal(image.querySelectorAll('.storyPreviewFrame-active').length, 1);
    assert.equal(image.querySelector('.storyPreviewFrame-active'), image.querySelectorAll('.storyPreviewFrame')[0]);
    assert.equal(timers.size, 1);

    const tick = timers.values().next().value;
    tick();
    assert.equal(image.querySelector('.storyPreviewFrame-active'), image.querySelectorAll('.storyPreviewFrame')[1]);
    card.getBoundingClientRect = () => ({ top: -300, bottom: -100, left: 0, right: 200 });
    timers.values().next().value();
    assert.equal(card.classList.contains('storyPreviewPlaying'), false);
    assert.equal(timers.size, 0);

    card.getBoundingClientRect = () => ({ top: 0, bottom: 200, left: 0, right: 200 });
    intersections[1].enter(card);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(card.classList.contains('storyPreviewPlaying'), true);
    intersections[1].callback([{ target: card, isIntersecting: false }]);
    assert.equal(card.classList.contains('storyPreviewPlaying'), false);
    assert.equal(timers.size, 0);
    dom.window.close();
});
