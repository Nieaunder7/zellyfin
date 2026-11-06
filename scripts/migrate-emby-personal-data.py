"""Copy collections and favorites via local APIs, matching only unambiguous paths.

Defaults to a read-only plan. --apply adds destination data; never deletes source
data, replaces destination collections, or starts a library scan.
"""
import argparse
import collections
import datetime
import json
import ntpath
import os
from pathlib import Path
import shutil
import sqlite3
import urllib.parse
import urllib.request


class Server:
    def __init__(self, base, database, query):
        self.base = base
        self.token = None
        with sqlite3.connect(database.resolve().as_uri() + '?mode=ro', uri=True) as db:
            tokens = [row[0] for row in db.execute(query)]
        for token in tokens:
            self.token = token
            try:
                users = self.api('Users')
                if len(users) != 1 or not users[0].get('Policy', {}).get('IsAdministrator'):
                    continue
                self.user = users[0]
                return
            except Exception:
                continue
        raise RuntimeError('A unique local administrator account/session is required; specify account mapping before multi-user migration.')

    def api(self, route, method='GET', **query):
        url = self.base + route
        if query:
            url += '?' + urllib.parse.urlencode(query)
        request = urllib.request.Request(url, method=method, headers={'X-Emby-Token': self.token})
        with urllib.request.urlopen(request, timeout=90) as response:
            data = response.read()
        return json.loads(data) if data else None

    def items(self, **query):
        result = []
        while True:
            page = self.api('Users/' + self.user['Id'] + '/Items',
                            **dict(Fields='Path', EnableImages='false', **query),
                            StartIndex=len(result), Limit=500)
            result.extend(page['Items'])
            if len(result) >= page['TotalRecordCount']:
                return result
            if not page['Items']:
                raise RuntimeError('Incomplete item pagination')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--runtime', required=True, type=Path)
    p.add_argument('--unc-root', required=True)
    p.add_argument('--mapped-drive', default='Z:')
    p.add_argument('--apply', action='store_true')
    args = p.parse_args()
    source_root = Path(os.environ['APPDATA']) / 'Emby-Server/programdata'
    source = Server('http://127.0.0.1:8096/emby/', source_root / 'data/authentication.db',
                    'SELECT AccessToken FROM Tokens_2 WHERE IsActive=1 ORDER BY DateLastActivityInt DESC')
    target = Server('http://127.0.0.1:8097/', args.runtime / 'data/data/jellyfin.db',
                    'SELECT AccessToken FROM Devices ORDER BY DateLastActivity DESC')

    def key(item):
        path = item.get('Path')
        if not path:
            return None
        path = ntpath.normpath(path)
        drive, tail = ntpath.splitdrive(path)
        if drive.casefold() == args.mapped_drive.casefold():
            path = args.unc_root.rstrip('\\/') + '\\' + tail.lstrip('\\/')
        # Allow Emby generic videos to match Jellyfin Movie/Episode entries.
        kind = 'Video' if item.get('MediaType') == 'Video' or item['Type'] in ('Video', 'Movie', 'Episode', 'MusicVideo') else item['Type']
        return (ntpath.normcase(path).casefold(), kind)

    output = args.runtime / 'migrations' / ('emby-personal-' + datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
    output.mkdir(parents=True)
    def save(name, value):
        (output / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

    source_collections = source.items(Recursive='true', IncludeItemTypes='BoxSet')
    source_favorites = source.items(Recursive='true', Filters='IsFavorite')
    target_items = target.items(Recursive='true')
    by_path = collections.defaultdict(list)
    by_name = collections.defaultdict(list)
    for item in target_items:
        if key(item):
            by_path[key(item)].append(item)
        if item['Type'] == 'BoxSet':
            by_name[item['Name']].append(item)
    unresolved = []
    def match(item, context):
        candidates = by_path.get(key(item), [])
        if len(candidates) == 1:
            return candidates[0]['Id']
        unresolved.append(dict(context=context, sourceId=item['Id'], name=item['Name'], path=item.get('Path'),
                               reason='ambiguous' if candidates else 'not_indexed', candidates=[x['Id'] for x in candidates]))
        return None

    plans = []
    for collection in source_collections:
        children = source.items(ParentId=collection['Id'], Recursive='false')
        save('source-collection-' + collection['Id'] + '.json', dict(collection=collection, items=children))
        matched = [match(item, 'collection:' + collection['Id']) for item in children]
        existing = by_name.get(collection['Name'], [])
        if len(existing) > 1:
            unresolved.append(dict(context='collection', sourceId=collection['Id'], name=collection['Name'], reason='duplicate_collection_name'))
            continue
        plans.append(dict(sourceId=collection['Id'], name=collection['Name'], sourceCount=len(children),
                          targetId=existing[0]['Id'] if existing else None,
                          items=list(dict.fromkeys(x for x in matched if x))))
    favorites = []
    collection_favorites = []
    for item in source_favorites:
        if item['Type'] == 'BoxSet':
            collection_favorites.append(item['Id'])
        else:
            dest = match(item, 'favorite')
            if dest:
                favorites.append(dest)
    save('source-favorites.json', source_favorites)
    save('destination-favorites-before.json', [x for x in target_items if x.get('UserData', {}).get('IsFavorite')])
    save('plan.json', dict(sourceUser=source.user['Name'], targetUser=target.user['Name'], collections=plans,
                           favorites=favorites, collectionFavorites=collection_favorites, unresolved=unresolved))
    summary = dict(sourceUser=source.user['Name'], targetUser=target.user['Name'], sourceCollections=len(source_collections),
                   plannedCollections=len(plans), sourceFavorites=len(source_favorites), matchedFavorites=len(favorites),
                   collectionMembers=sum(x['sourceCount'] for x in plans), matchedMembers=sum(len(x['items']) for x in plans),
                   unresolved=len(unresolved), applied=False)
    print(json.dumps(summary), flush=True)
    if not args.apply:
        print('Plan saved:', output)
        return

    # Consistent SQLite backup and collection files before destination writes.
    with sqlite3.connect((args.runtime.resolve() / 'data/data/jellyfin.db').as_uri() + '?mode=ro', uri=True) as db:
        with sqlite3.connect(output / 'jellyfin-before.db') as backup:
            db.backup(backup)
    collection_dir = args.runtime / 'data/data/collections'
    if collection_dir.exists():
        shutil.copytree(collection_dir, output / 'collections-before')
    journal = []
    def record(value):
        journal.append(value)
        save('changes.json', journal)
    for plan in plans:
        if not plan['targetId']:
            result = target.api('Collections', 'POST', name=plan['name'], ids=','.join(plan['items'][:50]))
            plan['targetId'] = result['Id']
            record(dict(action='create_collection', sourceId=plan['sourceId'], targetId=plan['targetId']))
        current = {x['Id'] for x in target.items(ParentId=plan['targetId'], Recursive='false')}
        missing = [x for x in plan['items'] if x not in current]
        for offset in range(0, len(missing), 50):
            batch = missing[offset:offset + 50]
            target.api('Collections/' + plan['targetId'] + '/Items', 'POST', ids=','.join(batch))
            record(dict(action='add_members', targetId=plan['targetId'], items=batch))
        actual = {x['Id'] for x in target.items(ParentId=plan['targetId'], Recursive='false')}
        assert set(plan['items']).issubset(actual), 'Collection verification failed'
    collection_map = {x['sourceId']: x['targetId'] for x in plans}
    favorites.extend(collection_map[x] for x in collection_favorites if x in collection_map)
    before = {x['Id'] for x in target_items if x.get('UserData', {}).get('IsFavorite')}
    for item_id in sorted(set(favorites) - before):
        target.api('Users/' + target.user['Id'] + '/FavoriteItems/' + item_id, 'POST')
        record(dict(action='add_favorite', targetId=item_id))
    actual_favorites = {x['Id'] for x in target.items(Recursive='true', Filters='IsFavorite')}
    assert (before | set(favorites)).issubset(actual_favorites), 'Favorite verification failed'
    summary.update(applied=True, addedFavorites=len(set(favorites) - before),
                   verifiedCollections=len(plans), destinationFavorites=len(actual_favorites))
    save('result.json', dict(summary=summary, collections=plans, unresolved=unresolved))
    print(json.dumps(summary), flush=True)
    print('Backup and results:', output)


if __name__ == '__main__':
    main()
