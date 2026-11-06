const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE');
const { JSDOM } = require(path.join(web, 'node_modules/jsdom'));
const modulePromise = import('data:text/javascript;base64,' + Buffer.from(
    fs.readFileSync(path.join(web, 'src/scripts/sceneAnalysis.js'), 'utf8')).toString('base64'));

function setup() {
    const dom = new JSDOM('<section class="sceneAnalysisDetail hide"><p class="sceneAnalysisStatus"></p><div class="sceneAnalysisContent"></div></section>');
    global.document = dom.window.document;
    const page = dom.window.document.body;
    return { dom, page, section: page.querySelector('.sceneAnalysisDetail') };
}

test('video details show partial progress, tags, frames, and an explicit scene jump', async () => {
    const { renderSceneAnalysis, clearSceneAnalysis } = await modulePromise;
    const { page, section } = setup();
    let playOptions;
    const item = { Id: 'abc', MediaType: 'Video' };
    renderSceneAnalysis(page, item, {
        getUrl: (route, query) => `${route}${query ? '?token=test' : ''}`,
        accessToken: () => 'test',
        getJSON: async () => ({
            status: 'running', completedSamples: 1, expectedSamples: 3,
            scenes: [{ timeTicks: 750000000, description: '실내 침대', tags: ['실내', '침대'], imageUrl: 'SceneAnalysis/frame.jpg' }]
        })
    }, { play: options => { playOptions = options; } });
    await new Promise(resolve => setImmediate(resolve));
    assert.match(section.querySelector('.sceneAnalysisStatus').textContent, /1\/3/);
    assert.match(section.textContent, /일부 장면/);
    assert.equal(section.querySelectorAll('.sceneAnalysisTags span').length, 2);
    assert.match(section.querySelector('img').src, /token=test/);
    section.querySelector('.sceneAnalysisJump').click();
    assert.equal(playOptions.startPositionTicks, 750000000);
    assert.equal(playOptions.sceneJump, true);
    clearSceneAnalysis(page);
});

test('non-video items hide analysis and do not request it', async () => {
    const { renderSceneAnalysis } = await modulePromise;
    const { page, section } = setup();
    renderSceneAnalysis(page, { Id: 'music', MediaType: 'Audio' }, {
        getJSON: () => { throw new Error('Unexpected request'); }
    }, {});
    assert.equal(section.classList.contains('hide'), true);
});
