const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Pass the patched, pinned upstream checkout; exercise its actual functions.
const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE to the patched web checkout');
const source = fs.readFileSync(path.join(web, 'src/controllers/list.js'), 'utf8');
const context = vm.createContext({
    globalize: { translate: x => x },
    CollectionType: { Homevideos: 'homevideos' },
    ItemSortBy: new Proxy({}, { get: (_, x) => x }),
    ServerConnections: { getApiClient: () => ({
        getCurrentUserId: () => 'user',
        getItems: (_, query) => ({ route: 'Items', query }),
        getUrl: (route, query) => ({ route, query }),
        getJSON: x => x
    }) }
});
vm.runInContext(source.replace(/^import .*;\r?\n/gm, '')
    .replace('export default ItemsView;', 'globalThis.ItemsView = ItemsView;'), context);
const root = { Id: 'library', Type: 'CollectionFolder', CollectionType: 'homevideos' };
function instance(sortBy, sortOrder, mode = 'videos') {
    const obj = Object.create(context.ItemsView.prototype);
    Object.assign(obj, {
        currentItem: root, params: { parentId: root.Id }, homeVideosViewMode: mode,
        getSortValues: () => ({ sortBy, sortOrder }), getFilters: () => ({}),
        setFilterStatus: () => {}, alphaPicker: { value: () => 'A' }
    });
    return obj;
}
const options = instance('SortName', 'Ascending').getSortMenuOptions();
test('file size is an available sort option', () => {
    assert(options.some(x => x.value === 'FileSize'));
});
for (const option of options) {
    for (const direction of ['Ascending', 'Descending']) {
        test(`${option.name}: ${direction} preserves sorting, scope and pagination`, () => {
            const obj = instance(option.value, direction);
            const result = context.getItems(obj, obj.params, root, null, 100, 100);
            assert.equal(result.query.SortBy, option.value);
            assert.equal(result.query.SortOrder, direction);
            assert.equal(result.query.ParentId, root.Id);
            assert.equal(result.query.Recursive, true);
            assert.equal(result.query.IncludeItemTypes, 'Video');
            assert.equal(result.query.StartIndex, 100);
            assert.equal(result.query.Limit, 100);
            assert.equal(Boolean(result.query.NameStartsWith),
                option.value === 'SortName' || option.value === 'IsFolder,SortName');
        });
    }
}
for (const sort of ['SeekCount', 'FileSize']) {
    test(`${sort}: folder view does not silently change sort or recurse`, () => {
        const obj = instance(sort, 'Descending', 'folders');
        obj.getFilters = () => ({ IsFavorite: true, IsUnplayed: true, IsSD: true });
        const { query, route } = context.getItems(obj, obj.params, root, null, 0, 100);
        assert.equal(route, 'SeekStatistics/Items');
        assert.equal(query.SortBy, sort);
        assert.equal(query.Recursive, false);
        assert.equal(query.IsHD, false);
        assert(query.Filters.includes('IsFavorite'));
        assert(query.Filters.includes('IsUnplayed'));
    });
}

test('sort change after scrolling resets page and discards an older response', async () => {
    const start = source.indexOf('        function fetchData() {');
    const end = source.indexOf('\n        function ', start + 20);
    const fetchSource = source.slice(start, end);
    const pending = [];
    let sort = 'SortName';
    const harness = vm.createContext({
        self: { getSortValues: () => ({ sortBy: sort }), getFilters: () => ({}),
            homeVideosViewMode: 'videos' }, params: {},
        usesNameSort: () => false, updateAlphaPickerState: () => {},
        getItems: (_, __, ___, ____, offset) => new Promise(resolve => pending.push({ resolve, offset })),
        disconnectInfiniteScrollObserver: () => {}, updatePaging: () => {}
    });
    vm.runInContext(`let query = { StartIndex: 200, Limit: 100 };
        let isLoading, loadedItems = [], totalRecordCount, listQueryKey;
        let requestGeneration = 0, latestRequest;
        ${fetchSource}
        globalThis.fetch = fetchData;`, harness);
    const oldRequest = harness.fetch();
    sort = 'FileSize';
    const newRequest = harness.fetch();
    assert.equal(pending[1].offset, 0);
    pending[1].resolve({ Items: [{ Id: 'new' }], StartIndex: 0, TotalRecordCount: 1 });
    pending[0].resolve({ Items: [{ Id: 'old' }], StartIndex: 0, TotalRecordCount: 1 });
    assert.equal((await newRequest).Items[0].Id, 'new');
    assert.equal((await oldRequest).Items[0].Id, 'new');
});
