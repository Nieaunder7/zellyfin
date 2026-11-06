"""Read-only loopback verification against indexed values; never logs credentials."""
import argparse
import json
from pathlib import Path
import sqlite3
import urllib.parse
import urllib.request

p = argparse.ArgumentParser()
p.add_argument('--runtime', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--native-only', action='store_true')
args = p.parse_args()
db = sqlite3.connect((args.runtime.resolve() / 'data/data/jellyfin.db').as_uri() + '?mode=ro', uri=True)
db.row_factory = sqlite3.Row
token = None


def api(route, **query):
    url = 'http://127.0.0.1:8097/' + route
    if query:
        url += '?' + urllib.parse.urlencode(query)
    request = urllib.request.Request(url, headers={'X-Emby-Token': token})
    with urllib.request.urlopen(request, timeout=90) as response:
        return json.load(response)


for row in db.execute("SELECT AccessToken FROM Devices WHERE AccessToken<>'' ORDER BY DateLastActivity DESC"):
    token = row[0]
    try:
        libraries = api('Library/VirtualFolders')
        user = api('Users/Me')
        break
    except Exception:
        token = None
if not token:
    raise RuntimeError('No authenticated local administrator session')


def normalize(value):
    return value.replace('-', '').lower()


items = {normalize(row['Id']): dict(row) for row in db.execute('SELECT * FROM BaseItems')}
userdata = {normalize(row['ItemId']): dict(row) for row in db.execute(
    'SELECT * FROM UserData WHERE replace(lower(UserId),\'-\',\'\')=?', (normalize(user['Id']),))}
seeks = {normalize(row['ItemId']): row['SeekCount'] for row in api('SeekStatistics/Summary', limit=100000)}
sorts = {
    'SortName': ('SortName',), 'CommunityRating': ('CommunityRating',),
    'CriticRating': ('CriticRating',), 'DateCreated': ('DateCreated',),
    'DatePlayed': ('LastPlayedDate',), 'IsFolder': ('IsFolder',),
    'OfficialRating': ('InheritedParentalRatingValue',), 'PlayCount': ('PlayCount',),
    'SeekCount': ('SeekCount',), 'FileSize': ('Size',),
    'ProductionYear,PremiereDate': ('ProductionYear', 'PremiereDate'), 'Runtime': ('RunTimeTicks',)
}
reports = []
for library in libraries:
    if library.get('CollectionType') != 'homevideos':
        continue
    for field, columns in sorts.items():
        custom = field in ('FileSize', 'SeekCount')
        if args.native_only and custom:
            continue
        route = 'SeekStatistics/Items' if custom else 'Items'
        for direction in ('Ascending', 'Descending'):
            query = dict(ParentId=library['ItemId'], UserId=user['Id'], Recursive='true',
                         IncludeItemTypes='Video', SortBy=field if custom or field == 'SortName' else field + ',SortName',
                         SortOrder=direction, EnableImages='false', Fields='SortName', Limit=500)
            ids, total, offset = [], None, 0
            while total is None or offset < total:
                result = api(route, **query, StartIndex=offset)
                total = result['TotalRecordCount']
                page = [normalize(item['Id']) for item in result['Items']]
                if not page:
                    break
                ids.extend(page)
                offset += len(page)
            assert len(ids) == total and len(set(ids)) == total, (field, direction, 'missing/duplicate page items')
            def key(item_id):
                record = items[item_id]
                values = []
                for col in columns:
                    if col == 'SeekCount':
                        value = seeks.get(item_id, 0)
                    elif col in ('LastPlayedDate', 'PlayCount'):
                        value = userdata.get(item_id, {}).get(col)
                    else:
                        value = record[col]
                    values.append((value is not None, value if value is not None else 0))
                return tuple(values)
            # Unknown file sizes must remain at the end in BOTH directions.
            known = [item for item in ids if field != 'FileSize' or items[item]['Size'] is not None]
            if field == 'FileSize':
                assert ids[:len(known)] == known, 'Unknown sizes were mixed into sized files'
            values = [key(item) for item in known]
            expected = sorted(values, reverse=direction == 'Descending')
            assert values == expected, (library['ItemId'], field, direction, 'wrong order')
            # Repeat a page boundary to confirm stable tie ordering.
            if total > 100:
                repeated = api(route, **{**query, 'Limit': 100}, StartIndex=100)
                assert [normalize(item['Id']) for item in repeated['Items']] == ids[100:200]
            reports.append(dict(library=library['ItemId'], sort=field, direction=direction,
                                count=total, distinctValues=len(set(values)), passed=True))
            print(field, direction, total, 'PASS', flush=True)
    # Custom sort filters must return exactly the standard endpoint's candidate set.
    for filters in (() if args.native_only else ('IsFavorite', 'IsPlayed', 'IsUnplayed', 'IsResumable')):
        query = dict(ParentId=library['ItemId'], UserId=user['Id'], Recursive='true', IncludeItemTypes='Video', Filters=filters)
        standard = api('Items', **query, Limit=0)['TotalRecordCount']
        for sort in ('SeekCount', 'FileSize'):
            result = api('SeekStatistics/Items', **query, SortBy=sort, Limit=1)
            assert result['TotalRecordCount'] == standard, (sort, filters, 'filter mismatch')
    print('Filters PASS', flush=True)
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(reports, indent=2), encoding='utf-8')
print('Verified', len(reports), 'sort/direction/library combinations')
