const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE');
const pagerModule = import('data:text/javascript;base64,' + Buffer.from(
    fs.readFileSync(path.join(web, 'src/scripts/pagedItems.js'), 'utf8')).toString('base64'));
const items = n => Array.from({ length: n }, (_, i) => ({ Id: String(i) }));

test('605 favorites are consumed in bounded pages without loss or duplicates', async () => {
    const { createPagedItems } = await pagerModule;
    const rows = items(605), requests = [];
    const pager = createPagedItems(async (start, limit) => {
        requests.push([start, limit]);
        return { Items: rows.slice(start, start + limit), TotalRecordCount: rows.length };
    });
    let result = await pager.fetch();
    while (pager.hasMore) result = await pager.fetch(true);
    assert.deepEqual(result.Items, rows);
    assert.deepEqual(requests.map(x => x[0]), [0, 100, 200, 300, 400, 500, 600]);
    assert(requests.every(x => x[1] === 100));
});

test('each favorites page exposes only newly added items for DOM append', async () => {
    const { createPagedItems } = await pagerModule;
    const rows = items(5);
    const pager = createPagedItems(async (start, limit) => ({
        Items: rows.slice(start, start + limit), TotalRecordCount: rows.length
    }), 3);
    const first = await pager.fetch();
    const second = await pager.fetch(true);
    assert.deepEqual(first.AppendedItems.map(item => item.Id), ['0', '1', '2']);
    assert.deepEqual(second.AppendedItems.map(item => item.Id), ['3', '4']);
    assert.deepEqual(second.Items.map(item => item.Id), ['0', '1', '2', '3', '4']);
});

test('changing the batch size restarts at zero without disposing the rendered container', async () => {
    const { createPagedItems } = await pagerModule;
    const requests = [];
    const pager = createPagedItems(async (start, limit) => {
        requests.push([start, limit]);
        return { Items: items(250).slice(start, start + limit), TotalRecordCount: 250 };
    }, 50);
    await pager.fetch();
    await pager.fetch(true);
    pager.reset(200);
    const refreshed = await pager.fetch();
    assert.deepEqual(requests, [[0, 50], [50, 50], [0, 200]]);
    assert.equal(refreshed.Items.length, 200);
    assert.equal(pager.hasMore, true);
});
test('removing a favorite refreshes the loaded range and preserves the next offset', async () => {
    const { createPagedItems } = await pagerModule;
    let rows = items(205);
    const pager = createPagedItems(async (start, limit) => ({ Items: rows.slice(start, start + limit), TotalRecordCount: rows.length }));
    await pager.fetch();
    await pager.fetch(true);
    rows = rows.filter(x => x.Id !== '50');
    const refresh = await pager.fetch();
    assert.equal(refresh.Items.length, 200);
    assert(!refresh.Items.some(x => x.Id === '50'));
    assert.deepEqual((await pager.fetch(true)).Items, rows);
});
test('a failed page retries the same offset', async () => {
    const { createPagedItems } = await pagerModule;
    let fail = true;
    const offsets = [];
    const pager = createPagedItems(async start => {
        offsets.push(start);
        if (start === 100 && fail) { fail = false; throw new Error('offline'); }
        return { Items: items(110).slice(start, start + 100), TotalRecordCount: 110 };
    });
    await pager.fetch();
    await assert.rejects(pager.fetch(true));
    assert.equal(pager.loading, false);
    assert.equal((await pager.fetch(true)).Items.length, 110);
    assert.deepEqual(offsets, [0, 100, 100]);
});
test('duplicate-only pages terminate rather than looping', async () => {
    const { createPagedItems } = await pagerModule;
    const pager = createPagedItems(async () => ({ Items: items(100), TotalRecordCount: 1000 }));
    await pager.fetch();
    assert.equal((await pager.fetch(true)).Items.length, 100);
    assert.equal(pager.hasMore, false);
});
test('a stale request cannot replace a newer refresh', async () => {
    const { createPagedItems } = await pagerModule;
    const pending = [];
    const pager = createPagedItems(() => new Promise(resolve => pending.push(resolve)));
    const old = pager.fetch();
    const fresh = pager.fetch();
    await Promise.resolve();
    pending[1]({ Items: [{ Id: 'new' }], TotalRecordCount: 1 });
    pending[0]({ Items: [{ Id: 'old' }], TotalRecordCount: 1 });
    assert.equal((await fresh).Items[0].Id, 'new');
    assert.equal((await old).Items[0].Id, 'new');
});
test('disposing prevents further requests', async () => {
    const { createPagedItems } = await pagerModule;
    let calls = 0;
    const pager = createPagedItems(() => { calls++; return { Items: [] }; });
    pager.dispose();
    await pager.fetch(true);
    assert.equal(calls, 0);
});
test('search merges repeated types and IDs without mutating earlier pages', () => {
    const ts = require(path.join(web, 'node_modules/typescript'));
    const source = fs.readFileSync(path.join(web, 'src/apps/stable/features/search/utils/mergePages.ts'), 'utf8');
    const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
    const module = { exports: {} };
    new Function('require', 'exports', compiled)(() => ({ sortSections: x => x }), module.exports);
    const before = [{ title: 'Videos', items: [{ Id: 'a' }] }];
    const result = module.exports.mergeSearchPage(before, [
        { title: 'Videos', items: [{ Id: 'a' }, { Id: 'b' }] },
        { title: 'Videos', items: [{ Id: 'b' }, { Id: 'c' }] }
    ]);
    assert.equal(result.added, 2);
    assert.equal(result.sections.length, 1);
    assert.deepEqual(result.sections[0].items.map(x => x.Id), ['a', 'b', 'c']);
    assert.equal(before[0].items.length, 1);
});
