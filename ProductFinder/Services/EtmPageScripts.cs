namespace ProductFinder.Services;

public static class EtmPageScripts
{
    public const string SearchSnapshot = """
        () => {
            const clean = v => (v || '').replace(/\s+/g,' ').trim();
            const visible = e => !!e && e.getClientRects().length > 0;
            const text = clean(document.body?.innerText);
            const error = /запрещенн(?:ого|ый) IP|доступ.{0,30}запрещен|access denied|forbidden/i.test(text)
                ? 'ЭТМ запретил доступ с этого подключения. Обход защиты не выполняется.' : '';
            const input = [...document.querySelectorAll('input[data-testid="input-search"]')].find(visible);
            const nodes = [...document.querySelectorAll('[data-testid^="diginetica-main-descr-"]')].filter(visible);
            const items = nodes.map(node => {
                const paragraphs = [...node.querySelectorAll('p')];
                const name = clean(paragraphs[0]?.textContent);
                const brand = paragraphs.find(p => /^Производитель\s*:/i.test(clean(p.textContent)));
                const container = node.closest('li') || node;
                const anchor = node.closest('a[href]') || [...container.querySelectorAll('a[href]')]
                    .find(a => !/\/brand\//i.test(a.getAttribute('href') || ''));
                let url = '';
                try {
                    const u = anchor ? new URL(anchor.getAttribute('href'), location.href) : null;
                    if (u && u.protocol === 'https:' && ['etm.ru','www.etm.ru'].includes(u.hostname) && !u.username && !u.password && !u.port) url = u.href;
                } catch {}
                return {name, manufacturer:clean(brand?.textContent).replace(/^Производитель\s*:\s*/i,''),
                    url, selectorKey:node.getAttribute('data-testid') || '', query:input?.value || ''};
            }).filter(x => x.name && /^diginetica-main-descr-\d+$/.test(x.selectorKey));
            return JSON.stringify({query:input?.value || '',items,error,empty:/ничего не найдено|товары не найдены|по вашему запросу.{0,50}не найден/i.test(text)});
        }
        """;
    public const string ProductSnapshot = """
        () => {
            const clean = v => (v || '').replace(/\s+/g,' ').trim();
            const rows = [...document.querySelectorAll('table tr')];
            const fields = {};
            for (const row of rows) {
                const cells = [...row.querySelectorAll(':scope > td')];
                if (cells.length < 2) continue;
                const key = clean(cells[0].textContent).replace(/:$/,'').trim().toLowerCase();
                if (key === 'артикул' || key === 'производитель') fields[key] = clean(cells[1].textContent);
            }
            const name = clean(document.querySelector('h1')?.textContent);
            return JSON.stringify({article:fields['артикул'] || '',name,manufacturer:fields['производитель'] || '',url:location.href});
        }
        """;
}
