const pluginId = '1e9d32f0-6b73-4c1c-a127-8f0c2cc78a13';
const pageSize = 200;

function api(path, options) {
    return window.ApiClient.ajax(Object.assign({
        type: 'GET',
        url: window.ApiClient.getUrl(path),
        dataType: 'json'
    }, options || {}));
}

function formatBytes(value) {
    const bytes = Number(value || 0);
    if (!bytes) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    return `${(bytes / Math.pow(1024, index)).toFixed(index > 1 ? 1 : 0)} ${units[index]}`;
}

function formatRuntime(ticks) {
    if (!ticks) return '-';
    const seconds = Math.round(Number(ticks) / 10000000);
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const remainder = seconds % 60;
    return hours > 0
        ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`
        : `${minutes}:${String(remainder).padStart(2, '0')}`;
}

function formatDateTime(value) {
    if (!value) return '-';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '-';
    return date.toLocaleString('ko-KR', {
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit'
    });
}

function classificationLabel(value) {
    return {
        LikelyDuplicate: '유력 중복',
        AlternateEncode: '다른 인코딩',
        Multipart: '분할 영상'
    }[value] || value;
}

function statusLabel(value) {
    return { New: '신규', Reviewed: '검토 완료', Ignored: '무시' }[value] || value;
}

function field(object, pascalName) {
    if (!object) return undefined;
    const camelName = pascalName.charAt(0).toLowerCase() + pascalName.slice(1);
    return object[camelName] !== undefined ? object[camelName] : object[pascalName];
}

function appendTextCell(row, value) {
    const cell = document.createElement('td');
    cell.className = 'detailTableBodyCell';
    cell.textContent = value;
    row.appendChild(cell);
    return cell;
}

function styleGroupControl(control) {
    control.style.background = '#2b2b2b';
    control.style.color = '#f5f5f5';
    control.style.border = '1px solid #777';
    control.style.borderRadius = '.25rem';
    control.style.padding = '.55rem .8rem';
}

function representativeChapters(chapters, maximum) {
    if (chapters.length <= maximum) return chapters;
    const selected = [];
    for (let index = 0; index < maximum; index++) {
        const chapterIndex = Math.round(index * (chapters.length - 1) / (maximum - 1));
        selected.push(chapters[chapterIndex]);
    }
    return selected;
}

function createGroup(group, refresh, keepSelections, onSelectionChanged) {
    const productCode = field(group, 'ProductCode');
    const fileCount = field(group, 'FileCount');
    const groupClassification = field(group, 'Classification');
    const groupStatus = field(group, 'Status');
    const potentialSavingsBytes = field(group, 'PotentialSavingsBytes');
    const groupItems = field(group, 'Items') || [];
    const card = document.createElement('section');
    card.className = 'paperList';
    card.style.marginBottom = '1rem';
    card.style.padding = '.9rem 1rem';

    const header = document.createElement('div');
    header.style.display = 'grid';
    header.style.gridTemplateColumns = 'minmax(8rem,1fr) repeat(4,minmax(7rem,auto))';
    header.style.gap = '.75rem';
    header.style.fontWeight = '600';
    header.innerHTML = '<span></span><span></span><span></span><span></span><span></span>';
    header.children[0].textContent = productCode;
    header.children[1].textContent = `${fileCount}개 파일`;
    header.children[2].textContent = classificationLabel(groupClassification);
    header.children[3].textContent = statusLabel(groupStatus);
    header.children[4].textContent = `예상 ${formatBytes(potentialSavingsBytes)}`;
    card.appendChild(header);

    const actions = document.createElement('div');
    actions.style.margin = '1rem 0';
    actions.style.display = 'flex';
    actions.style.flexWrap = 'wrap';
    actions.style.gap = '.5rem';
    const statusSelect = document.createElement('select');
    statusSelect.innerHTML = '<option value="New">신규</option><option value="Reviewed">검토 완료</option><option value="Ignored">무시</option>';
    statusSelect.value = groupStatus;
    styleGroupControl(statusSelect);
    statusSelect.addEventListener('change', async () => {
        await api(`DuplicateMedia/Groups/${encodeURIComponent(productCode)}/Status`, {
            type: 'PUT',
            contentType: 'application/json',
            data: JSON.stringify({ status: statusSelect.value })
        });
        await refresh();
    });
    actions.appendChild(statusSelect);

    const selectionSummary = document.createElement('span');
    selectionSummary.style.alignSelf = 'center';
    selectionSummary.style.marginLeft = '.5rem';
    const keptIds = keepSelections.get(productCode) || new Set();
    selectionSummary.textContent = keptIds.size
        ? `보존 ${keptIds.size}개 · 삭제 대상 ${groupItems.length - keptIds.size}개`
        : '보존할 파일을 하나 이상 선택하십시오.';
    actions.appendChild(selectionSummary);
    card.appendChild(actions);

    const wrapper = document.createElement('div');
    wrapper.style.overflowX = 'auto';
    const table = document.createElement('table');
    table.className = 'detailTable';
    table.style.width = '100%';
    const head = document.createElement('thead');
    head.innerHTML = '<tr><th>보존</th><th>이름</th><th>크기</th><th>생성 시각</th><th>재생시간</th><th>해상도</th><th>형식</th><th>경로</th></tr>';
    table.appendChild(head);
    const body = document.createElement('tbody');

    groupItems.forEach(item => {
        const row = document.createElement('tr');
        row.className = 'detailTableBodyRow detailTableBodyRow-shaded';
        const itemName = field(item, 'Name') || '-';
        const itemPath = field(item, 'Path') || '-';
        const width = field(item, 'Width');
        const height = field(item, 'Height');
        const selectCell = appendTextCell(row, '');
        const keepCheckbox = document.createElement('input');
        keepCheckbox.type = 'checkbox';
        keepCheckbox.checked = keptIds.has(field(item, 'ItemId'));
        keepCheckbox.setAttribute('aria-label', `${productCode}에서 ${itemName} 보존`);
        keepCheckbox.addEventListener('change', () => {
            const nextKeptIds = new Set(keepSelections.get(productCode) || []);
            if (keepCheckbox.checked) {
                nextKeptIds.add(field(item, 'ItemId'));
            } else {
                nextKeptIds.delete(field(item, 'ItemId'));
            }
            if (nextKeptIds.size) keepSelections.set(productCode, nextKeptIds);
            else keepSelections.delete(productCode);
            onSelectionChanged();
        });
        selectCell.appendChild(keepCheckbox);
        appendTextCell(row, itemName);
        appendTextCell(row, formatBytes(field(item, 'SizeBytes')));
        appendTextCell(row, formatDateTime(field(item, 'DateCreatedUtc')));
        appendTextCell(row, formatRuntime(field(item, 'RuntimeTicks')));
        appendTextCell(row, width && height ? `${width}x${height}` : '-');
        appendTextCell(row, field(item, 'Container') || '-');
        const pathCell = appendTextCell(row, '');
        const pathText = document.createElement('div');
        pathText.textContent = itemPath;
        pathText.style.wordBreak = 'break-all';
        pathCell.appendChild(pathText);
        body.appendChild(row);

        const chapters = representativeChapters(field(item, 'ChapterImages') || [], 5);
        if (chapters.length) {
            const chapterRow = document.createElement('tr');
            chapterRow.className = 'detailTableBodyRow';
            const chapterCell = document.createElement('td');
            chapterCell.colSpan = 8;
            chapterCell.style.padding = '.5rem 1rem 1rem 3rem';
            const chapterTitle = document.createElement('div');
            chapterTitle.textContent = `챕터 이미지 ${field(item, 'ChapterImages').length}개 중 대표 ${chapters.length}개`;
            chapterTitle.style.marginBottom = '.4rem';
            chapterCell.appendChild(chapterTitle);
            const strip = document.createElement('div');
            strip.style.display = 'flex';
            strip.style.gap = '.5rem';
            strip.style.overflowX = 'auto';
            chapters.forEach(chapter => {
                const tile = document.createElement('figure');
                tile.style.margin = '0';
                tile.style.flex = '0 0 11rem';
                const image = document.createElement('img');
                const chapterIndex = Number(field(chapter, 'Index'));
                image.src = window.ApiClient.getScaledImageUrl(field(item, 'ItemId'), {
                    type: 'Chapter',
                    index: chapterIndex,
                    tag: field(chapter, 'ImageTag'),
                    maxWidth: 240,
                    quality: 80
                });
                image.loading = 'lazy';
                image.alt = `${itemName} ${formatRuntime(field(chapter, 'StartPositionTicks'))}`;
                image.style.width = '100%';
                image.style.aspectRatio = '16 / 9';
                image.style.objectFit = 'cover';
                image.style.borderRadius = '.25rem';
                image.addEventListener('error', () => tile.remove());
                const caption = document.createElement('figcaption');
                caption.textContent = formatRuntime(field(chapter, 'StartPositionTicks'));
                caption.style.textAlign = 'center';
                caption.style.fontSize = '.85rem';
                tile.appendChild(image);
                tile.appendChild(caption);
                strip.appendChild(tile);
            });
            chapterCell.appendChild(strip);
            chapterRow.appendChild(chapterCell);
            body.appendChild(chapterRow);
        }
    });

    table.appendChild(body);
    wrapper.appendChild(table);
    card.appendChild(wrapper);
    return card;
}

export default function(view) {
    let startIndex = 0;
    let refreshTimer;
    let currentGroups = [];
    const keepSelections = new Map();
    const results = view.querySelector('#duplicateMediaResults');
    const search = view.querySelector('#duplicateMediaSearch');
    const classification = view.querySelector('#duplicateMediaClassification');
    const status = view.querySelector('#duplicateMediaStatus');
    const batchSummary = view.querySelector('#duplicateMediaBatchSummary');
    const batchDeleteButton = view.querySelector('#duplicateMediaDeleteBatch');

    function selectedGroups() {
        return currentGroups.filter(group => {
            const keptIds = keepSelections.get(field(group, 'ProductCode'));
            const itemCount = (field(group, 'Items') || []).length;
            return keptIds && keptIds.size > 0 && keptIds.size < itemCount;
        });
    }

    function updateBatchSummary() {
        const selected = selectedGroups();
        let deleteCount = 0;
        let deleteBytes = 0;
        selected.forEach(group => {
            const productCode = field(group, 'ProductCode');
            const keepItemIds = keepSelections.get(productCode) || new Set();
            (field(group, 'Items') || []).forEach(item => {
                if (!keepItemIds.has(field(item, 'ItemId'))) {
                    deleteCount++;
                    deleteBytes += Number(field(item, 'SizeBytes') || 0);
                }
            });
        });
        batchSummary.textContent = selected.length
            ? `${selected.length}개 그룹 · ${deleteCount}개 파일(${formatBytes(deleteBytes)})이 삭제 대상입니다.`
            : '보존 파일을 선택한 그룹이 없습니다.';
        batchDeleteButton.disabled = selected.length === 0;
    }

    function renderGroups() {
        results.replaceChildren();
        if (!currentGroups.length) {
            const empty = document.createElement('p');
            empty.textContent = '표시할 중복 후보가 없습니다.';
            results.appendChild(empty);
        } else {
            currentGroups.forEach(group => results.appendChild(
                createGroup(group, refresh, keepSelections, renderGroups)));
        }
        updateBatchSummary();
    }

    function applyKeepRule(compare) {
        currentGroups.forEach(group => {
            const items = field(group, 'Items') || [];
            if (!items.length) return;
            const kept = items.reduce((best, current) => compare(
                Number(field(current, 'SizeBytes') || 0),
                Number(field(best, 'SizeBytes') || 0)) ? current : best);
            keepSelections.set(field(group, 'ProductCode'), new Set([field(kept, 'ItemId')]));
        });
        renderGroups();
    }

    async function loadSummary() {
        const summary = await api('DuplicateMedia/Summary');
        view.querySelector('#duplicateMediaGroupCount').textContent = field(summary, 'DuplicateGroupCount') || 0;
        view.querySelector('#duplicateMediaFileCount').textContent = field(summary, 'CandidateFileCount') || 0;
        view.querySelector('#duplicateMediaSavings').textContent = formatBytes(field(summary, 'PotentialSavingsBytes'));

        const run = field(summary, 'LatestRun');
        const runStatus = view.querySelector('#duplicateMediaRunStatus');
        if (!run) {
            runStatus.textContent = '아직 실행된 검사가 없습니다.';
            return false;
        }

        const runStatusValue = field(run, 'Status');
        const totalItems = Number(field(run, 'TotalItems') || 0);
        const scannedItems = Number(field(run, 'ScannedItems') || 0);
        const matchedItems = Number(field(run, 'MatchedItems') || 0);
        const error = field(run, 'Error');
        const progress = totalItems > 0 ? Math.round(scannedItems * 100 / totalItems) : 100;
        runStatus.textContent = runStatusValue === 'Running'
            ? `검사 중: ${scannedItems.toLocaleString()} / ${totalItems.toLocaleString()} (${progress}%)`
            : `최근 검사: ${runStatusValue} · ${scannedItems.toLocaleString()}개 확인 · ${matchedItems.toLocaleString()}개 고유코드 검출${error ? ` · ${error}` : ''}`;
        return runStatusValue === 'Running';
    }

    async function loadGroups() {
        const parameters = new URLSearchParams({
            startIndex: String(startIndex),
            limit: String(pageSize),
            search: search.value.trim(),
            status: status.value,
            classification: classification.value
        });
        const page = await api(`DuplicateMedia/Groups?${parameters.toString()}`);
        const pageItems = field(page, 'Items') || [];
        const pageStartIndex = Number(field(page, 'StartIndex') || 0);
        const totalRecordCount = Number(field(page, 'TotalRecordCount') || 0);
        currentGroups = pageItems;
        for (const code of [...keepSelections.keys()]) {
            if (!currentGroups.some(group => field(group, 'ProductCode') === code)) {
                keepSelections.delete(code);
            }
        }
        renderGroups();

        const first = totalRecordCount ? pageStartIndex + 1 : 0;
        const last = Math.min(pageStartIndex + pageItems.length, totalRecordCount);
        view.querySelector('#duplicateMediaPageLabel').textContent = `${first}-${last} / ${totalRecordCount}`;
        view.querySelector('#duplicateMediaPreviousButton').disabled = startIndex === 0;
        view.querySelector('#duplicateMediaNextButton').disabled = startIndex + pageSize >= totalRecordCount;
    }

    async function refresh() {
        const running = await loadSummary();
        await loadGroups();
        clearTimeout(refreshTimer);
        if (running) refreshTimer = setTimeout(refresh, 3000);
    }

    function resetAndRefresh() {
        startIndex = 0;
        keepSelections.clear();
        refresh();
    }

    view.addEventListener('viewshow', async () => {
        const config = await window.ApiClient.getPluginConfiguration(pluginId);
        view.querySelector('#duplicateMediaPattern').value = config.ProductCodePattern;
        view.querySelector('#duplicateMediaIncludeParent').checked = config.IncludeParentFolder;
        await refresh();
    });

    view.addEventListener('viewhide', () => clearTimeout(refreshTimer));
    view.querySelector('#duplicateMediaScanButton').addEventListener('click', async () => {
        await api('DuplicateMedia/Scan', { type: 'POST' });
        await refresh();
    });
    view.querySelector('#duplicateMediaRefreshButton').addEventListener('click', refresh);
    view.querySelector('#duplicateMediaKeepLargestAll').addEventListener('click', () =>
        applyKeepRule((current, best) => current > best));
    view.querySelector('#duplicateMediaKeepSmallestAll').addEventListener('click', () =>
        applyKeepRule((current, best) => current < best));
    view.querySelector('#duplicateMediaClearKeepAll').addEventListener('click', () => {
        keepSelections.clear();
        renderGroups();
    });
    batchDeleteButton.addEventListener('click', async () => {
        const selected = selectedGroups();
        if (!selected.length) return;
        let deleteCount = 0;
        let deleteBytes = 0;
        selected.forEach(group => {
            const keepItemIds = keepSelections.get(field(group, 'ProductCode')) || new Set();
            (field(group, 'Items') || []).forEach(item => {
                if (!keepItemIds.has(field(item, 'ItemId'))) {
                    deleteCount++;
                    deleteBytes += Number(field(item, 'SizeBytes') || 0);
                }
            });
        });
        const accepted = window.confirm(
            `${selected.length}개 그룹에서 보존 파일을 제외한 ${deleteCount}개 파일(${formatBytes(deleteBytes)})을 영구 삭제합니다.\n\n` +
            '휴지통을 거치지 않으며 복구할 수 없을 수 있습니다. 계속하시겠습니까?');
        if (!accepted) return;
        const expected = `영구삭제 ${selected.length}`;
        const confirmation = window.prompt(`마지막 확인: ${expected} 을(를) 정확히 입력하십시오.`);
        if (confirmation === null) return;
        if (confirmation.trim() !== expected) {
            window.alert('확인 문구가 일치하지 않아 삭제하지 않았습니다.');
            return;
        }

        batchDeleteButton.disabled = true;
        try {
            const result = await api('DuplicateMedia/DeleteBatch', {
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({
                    groups: selected.map(group => ({
                        productCode: field(group, 'ProductCode'),
                        keepItemIds: [...keepSelections.get(field(group, 'ProductCode'))]
                    })),
                    confirmation
                })
            });
            const deletedCount = Number(field(result, 'DeletedCount') || 0);
            const requestedCount = Number(field(result, 'RequestedCount') || 0);
            const errors = (field(result, 'Items') || []).map(item => field(item, 'Error')).filter(Boolean);
            window.alert(errors.length
                ? `${deletedCount}/${requestedCount}개 파일을 삭제했습니다.\n\n${errors.join('\n')}`
                : `${deletedCount}개 파일을 영구 삭제했습니다.`);
            keepSelections.clear();
            await refresh();
        } catch (error) {
            window.alert(`삭제하지 못했습니다: ${error && error.responseText ? error.responseText : error}`);
            updateBatchSummary();
        }
    });
    view.querySelector('#duplicateMediaExportButton').addEventListener('click', async () => {
        const csv = await api('DuplicateMedia/Export', { dataType: 'text' });
        const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = `duplicate-media-${new Date().toISOString().replace(/[:.]/g, '-')}.csv`;
        link.click();
        URL.revokeObjectURL(url);
    });
    view.querySelector('#duplicateMediaPreviousButton').addEventListener('click', () => {
        startIndex = Math.max(0, startIndex - pageSize);
        refresh();
    });
    view.querySelector('#duplicateMediaNextButton').addEventListener('click', () => {
        startIndex += pageSize;
        refresh();
    });
    search.addEventListener('input', resetAndRefresh);
    classification.addEventListener('change', resetAndRefresh);
    status.addEventListener('change', resetAndRefresh);
    view.querySelector('#duplicateMediaConfigForm').addEventListener('submit', async event => {
        event.preventDefault();
        Dashboard.showLoadingMsg();
        const config = await window.ApiClient.getPluginConfiguration(pluginId);
        config.ProductCodePattern = view.querySelector('#duplicateMediaPattern').value.trim();
        config.IncludeParentFolder = view.querySelector('#duplicateMediaIncludeParent').checked;
        const result = await window.ApiClient.updatePluginConfiguration(pluginId, config);
        Dashboard.processPluginConfigurationUpdateResult(result);
    });
}
