const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const web = process.env.JELLYFIN_WEB_SOURCE;
if (!web) throw new Error('Set JELLYFIN_WEB_SOURCE');
const source = fs.readFileSync(path.join(web, 'src/controllers/list.js'), 'utf8');
const context = vm.createContext({
    globalize: { translate: x => x },
    CollectionType: { Homevideos: 'homevideos' },
    ItemSortBy: new Proxy({}, { get: (_, key) => key }),
    ServerConnections: { getApiClient: () => ({
        getCurrentUserId: () => 'user',
        getUrl: (route, query) => ({ route, query }),
        getJSON: value => value
    }) }
});
vm.runInContext(source.replace(/^import .*;\r?\n/gm, '')
    .replace('export default ItemsView;', 'globalThis.ItemsView = ItemsView;'), context);
const library = { Id: 'library', Type: 'CollectionFolder', CollectionType: 'homevideos' };

for (const sort of ['DateCreated,SortName', 'FileSize', 'SeekCount']) {
    for (const batch of [50, 100, 200]) {
        test(`complete-only + nonfavorite + oldest sort query: ${sort}, ${batch}`, () => {
            const instance = Object.create(context.ItemsView.prototype);
            Object.assign(instance, {
                params: { parentId: library.Id }, currentItem: library, homeVideosViewMode: 'videos',
                getSortValues: () => ({ sortBy: sort, sortOrder: 'Ascending' }),
                getFilters: () => ({ AnalysisCompleteOnly: true, IsNotFavorite: true }),
                setFilterStatus: () => {}, alphaPicker: { value: () => 'A' }
            });
            const result = context.getItems(instance, instance.params, library, null, batch, batch);
            assert.equal(result.route, 'SeekStatistics/Items');
            assert.equal(result.query.AnalysisCompleteOnly, true);
            assert.equal(result.query.IsFavorite, false);
            assert.equal(result.query.SortBy, sort);
            assert.equal(result.query.SortOrder, 'Ascending');
            assert.equal(result.query.StartIndex, batch);
            assert.equal(result.query.Limit, batch);
            assert.equal(result.query.Recursive, true);
        });
    }
}
