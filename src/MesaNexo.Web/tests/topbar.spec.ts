import { test, expect, Page } from '@playwright/test';

const widths: [number, number][] = [[1024, 768], [1280, 720], [1280, 800], [1366, 768], [1440, 900], [1600, 900], [1920, 1080], [2560, 1440]];
const state = {
    siteId: 'local', user: { id: 'u1', name: 'Pizza de Peperoni', role: 'manager', permissions: {} },
    restaurant: { name: 'McMamadas', branchId: 'b1', revision: 1, modules: ['reservations', 'reviews', 'promotions'], settings: { color: '#f2b93b', stations: ['Cocina'], delayMinutes: 15, discountLimit: 20 } },
    branches: [{ id: 'b1', name: 'McMamadas' }], tables: [], visits: [], units: [], commands: [], notifications: [], users: [], products: [], categorys: [],
    cashs: [], requests: [], movements: [], audits: [], devices: [], sessions: [], folios: [], payments: [], connection: { central: 'pending' },
};

async function kiosk(page: Page, battery: any = { available: true, onBattery: false }, width = 1366, height = 768) {

    await page.setViewportSize({ width, height });
    await page.addInitScript(([fixture, power]) => {

        const w = window as any;
        w.battery = power;
        w.__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                const data: Record<string, any> = {
                    'window.present': { enabled: true }, 'window.ready': { visible: true }, 'power.status': { available: true }, 'power.battery': w.battery,
                    'network.status': { available: true, kind: 'wifi', name: 'Terraza', signal: 80, wifi: true }, 'profile.get': { ServerUrl: 'https://restaurant.invalid:8443' },
                    'motion.get': { reduced: false }, 'theme.get': { theme: 'light' }, status: { authenticated: true }, state: fixture, 'license.status': { state: 'active' }, 'updates.arrived': w.arrived ?? { arrived: false },
                };
                return { ok: true, data: data[p.command] ?? {} };

            }
        };

    }, [state, battery] as const);
    await page.goto('/');
    await expect(page.locator('main h1')).toBeVisible();

}
test('the amber pill travels to the chosen tab and the page follows', async ({ page }) => {

    await kiosk(page);
    const path = await page.evaluate(async () => {

        const pill = document.querySelector<HTMLElement>('.nav-pill')!, left: number[] = [];
        document.querySelector<HTMLElement>('button[aria-label="Bandeja de pendientes"]')!.click();
        const end = performance.now() + 500;
        while (performance.now() < end) { left.push(pill.getBoundingClientRect().left); await new Promise(requestAnimationFrame); }
        return left;

    });
    expect(new Set(path.map(Math.round)).size, 'posiciones intermedias').toBeGreaterThan(5);
    await expect(page.locator('main h1')).toHaveText('Bandeja de pendientes');
    const pill = await box(page, '.nav-pill'), tab = await box(page, 'button[aria-label="Bandeja de pendientes"]');
    expect(Math.abs(pill.x - tab.x)).toBeLessThan(2);
    expect(Math.abs(pill.width - tab.width)).toBeLessThan(2);

});
test('without a GPU the theme changes at once instead of animating the whole screen', async ({ page }) => {

    await kiosk(page);
    await page.evaluate(() => { document.documentElement.dataset.cpuDrawing = 'true'; (document as any).startViewTransition = () => { throw new Error('transición con el procesador'); }; });
    await page.getByRole('button', { name: /^Menú de/ }).click();
    await page.getByRole('button', { name: 'Oscuro' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

});
test('the scrollbar starts below the top bar', async ({ page }) => {

    await kiosk(page, undefined, 1024, 500);
    const bar = await box(page, '.topbar'), main = await box(page, 'main');
    expect(main.y).toBeCloseTo(bar.y + bar.height, 0);
    expect(main.y + main.height).toBeCloseTo(500, 0);
    expect(await page.evaluate(() => document.documentElement.scrollHeight <= window.innerHeight)).toBe(true);
    expect(await page.locator('main').evaluate(element => { element.scrollTop = 40; return element.scrollTop; })).toBeGreaterThan(0);

});
test('motion is chosen in the account menu only, and a poll without changes keeps the page', async ({ page }) => {

    await kiosk(page);
    await page.evaluate(() => { const w = window as any, invoke = w.__TAURI_INTERNALS__.invoke; w.__TAURI_INTERNALS__.invoke = (name: string, p: any) => p.command === 'state' ? Promise.resolve({ ok: true, data: { unchanged: true } }) : invoke(name, p); });
    await page.waitForTimeout(2300);
    await expect(page.locator('main h1')).toBeVisible();
    await page.getByRole('button', { name: /^Menú de/ }).click();
    await expect(page.getByRole('switch', { name: 'Reducir animaciones' })).toBeAttached();
    await page.getByRole('menuitem', { name: /^Versión/ }).click();
    const dialog = page.getByRole('dialog', { name: /^Culinary Restaurant/ });
    await expect(dialog).toBeVisible();
    await expect(dialog.getByText('Reducir animaciones')).toHaveCount(0);

});
const box = async (page: Page, selector: string) => (await page.locator(selector).boundingBox())!;
const apart = (a: any, b: any) => a.x + a.width <= b.x || b.x + b.width <= a.x || a.y + a.height <= b.y || b.y + b.height <= a.y;

for (const [width, height] of widths) test(`top bar never overlaps and the app fills ${width}x${height}`, async ({ page }) => {

    await kiosk(page, { available: true, onBattery: true, source: 'battery', percent: 64, seconds: 7800 }, width, height);
    await expect(page.locator('.battery-status')).toBeVisible();
    await page.evaluate(() => document.fonts.ready);
    const brand = await box(page, '.shell-brand'), nav = await box(page, '.studio-navigation'), right = await box(page, '.top-right');
    expect(apart(brand, nav), 'marca y navegación').toBe(true);
    expect(apart(nav, right), 'navegación y controles').toBe(true);
    expect(apart(brand, right), 'marca y controles').toBe(true);
    // The last item is what the photo showed under the status dot: it must stay inside the navigation.
    const items = await page.locator('.studio-navigation .nav-quick>button, .nav-section>summary').evaluateAll(all => all.map(e => e.getBoundingClientRect().toJSON()));
    const navRight = nav.x + nav.width;
    for (const item of items) expect(item.right).toBeLessThanOrEqual(navRight + 1);
    expect(await page.evaluate(() => { const box = document.querySelector('main')!; return box.scrollWidth <= box.clientWidth; })).toBe(true);
    const bar = await box(page, '.topbar'), main = await box(page, 'main.main');
    expect(bar.x).toBe(0);
    expect(bar.width).toBe(width);
    expect(main.width).toBe(width);
    expect(right.x + right.width).toBeGreaterThan(width - 40);
    await page.screenshot({ path: `../../evidence/topbar-${width}x${height}.png` });

});

test('the wide layout keeps a single row and a narrow one stacks after resizing', async ({ page }) => {

    await kiosk(page, undefined, 1920, 1080);
    await expect(page.locator('.topbar-in')).not.toHaveClass(/is-stacked/);
    await page.setViewportSize({ width: 1100, height: 800 });
    await expect(page.locator('.topbar-in')).toHaveClass(/is-stacked/);
    await page.setViewportSize({ width: 1920, height: 1080 });
    await expect(page.locator('.topbar-in')).not.toHaveClass(/is-stacked/);

});

test('changing page is one plain fade that waits for its first frame, with no blur, movement or delayed blocks', async ({ page }) => {

    await kiosk(page, undefined, 1366, 768);
    await page.getByRole('button', { name: 'Bandeja de pendientes' }).click();
    await expect(page.locator('main h1')).toHaveText('Bandeja de pendientes');
    const animations = await page.evaluate(() => document.getAnimations().map(a => {

        const effect = a.effect as KeyframeEffect;
        return { delay: effect.getTiming().delay, target: (effect.target as Element)?.className, props: effect.getKeyframes().flatMap(k => Object.keys(k)) };

    }).filter(a => /page-content|page-title/.test(a.target as string)));
    expect(animations.map(a => a.target).sort()).toEqual(['page-content', 'page-title ui-page-heading']);
    for (const animation of animations) {

        expect(animation.delay).toBe(0);
        expect(animation.props).toContain('opacity');
        expect(animation.props).not.toContain('filter');
        expect(animation.props).not.toContain('transform');

    }
    // Once done it lets go: nothing stays held on the page.
    await expect.poll(() => page.evaluate(() => document.getAnimations().filter(a => /page-content|page-title/.test(((a.effect as KeyframeEffect).target as Element)?.className)).length)).toBe(0);
    await expect(page.locator('.page-content')).toHaveCSS('opacity', '1');
    const css = await page.evaluate(() => [...document.styleSheets].flatMap(s => [...s.cssRules]).map(r => r.cssText).filter(t => /filter:\s*blur/.test(t) && /page-content|stagger|h1/.test(t)));
    expect(css).toEqual([]);

});

test('the tab pill keeps a flat tail past its visible end, and the page lets go during it', async ({ page }) => {

    await kiosk(page, undefined, 1366, 768);
    await page.getByRole('button', { name: 'Mesas y cuentas' }).click();
    const seen = await page.evaluate(() => document.querySelector('.nav-pill')!.getAnimations().map(a => { const e = a.effect as KeyframeEffect, f = e.getKeyframes(); return { fill: e.getTiming().fill, duration: e.getTiming().duration, same: f[f.length - 1].transform === f[f.length - 2].transform, visible: f[f.length - 2].computedOffset }; }));
    expect(seen).toEqual([expect.objectContaining({ fill: 'both', duration: 1340, same: true })]);
    expect(seen[0].visible).toBeCloseTo(340 / 1340);
    await expect.poll(() => page.evaluate(() => document.querySelector('.nav-pill')!.getAnimations().length), { timeout: 900 }).toBe(0);

});

test('a CSS entrance gets the tail and ends where the page would have put it; exits and loops are left alone', async ({ page }) => {

    await kiosk(page, undefined, 1366, 768);
    const held = await page.evaluate(() => new Promise<any>(resolve => {

        const box = document.createElement('div');
        box.style.cssText = 'animation:drop .2s ease both';
        const out = document.createElement('div');
        out.style.cssText = 'animation:fade-out 2s ease both';
        const loop = document.createElement('div');
        loop.style.cssText = 'animation:spin .2s linear infinite';
        document.body.append(box, out, loop);
        (window as any).entered = box;
        setTimeout(() => resolve({ box: box.getAnimations().map(a => a.effect!.getTiming().duration), out: out.getAnimations().map(a => a.effect!.getTiming().duration), loop: loop.getAnimations().map(a => a.effect!.getTiming().duration) }), 100);

    }));
    expect(held).toEqual({ box: [1200], out: [2000], loop: [200] });
    await expect.poll(() => page.evaluate(() => (window as any).entered.getAnimations().length), { timeout: 900 }).toBe(0);

});

test('nothing in the bar opens the native tooltip, which the kiosk draws as a white box', async ({ page }) => {

    await kiosk(page, undefined, 1366, 768);
    await expect(page.locator('.studio-navigation [title], .battery-status[title], .context-trigger[title]')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Mesas y cuentas' })).toBeVisible();

});

const signedInAs = async (page: Page, role: string, replies: Record<string, any>, extra: Record<string, any> = {}) => {

    await kiosk(page);
    await page.evaluate(([who, answers, more]) => {

        const w = window as any, invoke = w.__TAURI_INTERNALS__.invoke;
        w.calls = [];
        w.__TAURI_INTERNALS__.invoke = async (name: string, p: any) => {

            if (p.command !== 'state' && p.command !== 'status') w.calls.push(p.command);
            if (p.command === 'state') { const reply = await invoke(name, p); return { ok: true, data: { ...reply.data, user: { ...reply.data.user, role: who }, ...(more as any) } }; }
            if (p.command in (answers as any)) { const answer = (answers as any)[p.command]; return answer.error ? { ok: false, error: answer.error, status: 409 } : { ok: true, data: answer }; }
            return invoke(name, p);

        };

    }, [role, replies, extra]);

};
const openVersion = async (page: Page) => {

    await page.getByRole('button', { name: /^Menú de/ }).click();
    await page.getByRole('menuitem', { name: /^Versión/ }).click();
    return page.getByRole('dialog', { name: /^Culinary Restaurant/ });

};

test('a manager searches from Version and one confirmation downloads, signs everyone out and installs', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: true, version: '0.3.4', notes: 'Corrige la caja', bytes: 10, blocked: '', otherSessions: 2 }, 'updates.prepare': { update: true }, 'updates.install': { started: true } });
    const dialog = await openVersion(page);
    await dialog.getByRole('button', { name: 'Buscar actualización' }).click();
    await expect(dialog).toContainText('Versión 0.3.04 disponible');
    await expect(dialog).toContainText('Corrige la caja');
    await expect(dialog).toContainText('2 dispositivos más');
    await dialog.getByRole('button', { name: 'Actualizar ahora' }).click();
    await expect.poll(() => page.evaluate(() => (window as any).calls.filter((c: string) => c.startsWith('updates.') && !['updates.arrived', 'updates.status'].includes(c)))).toEqual(['updates.look', 'updates.prepare', 'updates.install']);
    // The app covers the screen with the progress until the installer takes over.
    const screen = page.getByRole('alertdialog', { name: 'Actualizando Culinary Restaurant' });
    await expect(screen).toContainText('Versión 0.3.04');
    await expect(screen.locator('li[aria-current="step"]')).toHaveText(/Instalar/);

});

test('an open account or register stops the update before anything downloads', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: true, version: '0.3.4', notes: '', bytes: 10, blocked: 'Hay 1 caja abierta en la sucursal; ciérrala para actualizar', otherSessions: 0 } });
    const dialog = await openVersion(page);
    await dialog.getByRole('button', { name: 'Buscar actualización' }).click();
    await expect(dialog).toContainText('Hay 1 caja abierta');
    await expect(dialog.getByRole('button', { name: 'Actualizar ahora' })).toHaveCount(0);
    await expect(dialog.getByRole('button', { name: 'Comprobar de nuevo' })).toBeVisible();

});

test('with nothing new the dialog says so, and only a manager can search', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: null, latestVersion: '0.3.4', currentVersion: '0.3.4' } });
    const dialog = await openVersion(page);
    await dialog.getByRole('button', { name: 'Buscar actualización' }).click();
    await expect(dialog).toContainText('Ya estás al día.');
    await expect(dialog).toContainText('Tienes la versión 0.3.04');

});

test('a device newer than the published version says so and installs nothing', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: null, latestVersion: '0.3.3', currentVersion: '0.3.4' } });
    const dialog = await openVersion(page);
    await dialog.getByRole('button', { name: 'Buscar actualización' }).click();
    await expect(dialog).toContainText('Tu equipo tiene una versión más nueva (0.3.04) que la publicada (0.3.03).');
    await expect(dialog.getByRole('button', { name: 'Actualizar ahora' })).toHaveCount(0);

});

test('with nothing published for the license the dialog says that instead', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: null, latestVersion: null, currentVersion: '0.3.4' } });
    const dialog = await openVersion(page);
    await dialog.getByRole('button', { name: 'Buscar actualización' }).click();
    await expect(dialog).toContainText('No hay versiones publicadas para tu licencia · tienes la 0.3.04.');

});

test('the server shows each step of its update and how it ended, once', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.status': { progress: { stage: 'backup', active: true, version: '0.3.5', at: '2026-11-07T10:00:00Z' } } });
    await page.evaluate(() => window.dispatchEvent(new Event('culinary-update-started')));
    const screen = page.getByRole('alertdialog', { name: 'Actualizando Culinary Restaurant' });
    await expect(screen).toContainText('Versión 0.3.05');
    await expect(screen.locator('li[aria-current="step"]')).toHaveText(/Respaldar la base de datos/);
    await expect(screen.locator('li.done')).toHaveCount(1);
    await page.screenshot({ path: '../../evidence/update-server-1360.png' });
    await page.evaluate(() => { const w = window as any, invoke = w.__TAURI_INTERNALS__.invoke; w.__TAURI_INTERNALS__.invoke = async (name: string, p: any) => p.command === 'updates.status' ? { ok: true, data: { progress: { stage: 'healthy', active: false, version: '0.3.5', at: '2026-11-07T10:05:00Z' } } } : invoke(name, p); });
    const done = page.getByRole('alertdialog', { name: 'Actualización terminada' });
    await expect(done).toContainText('Culinary Restaurant 0.3.05 ya está funcionando.');
    await done.getByRole('button', { name: 'Continuar' }).click();
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    await page.evaluate(() => window.dispatchEvent(new Event('culinary-update-started')));
    await page.waitForTimeout(1500);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);

});

test('a terminal keeps the update screen while the branch server updates', async ({ page }) => {

    await signedInAs(page, 'mesero', {}, { connection: { installing: { stage: 'installing', active: true, version: '0.3.5', at: '2026-11-07T10:00:00Z' } } });
    const screen = page.getByRole('alertdialog', { name: 'La sucursal se está actualizando' });
    await expect(screen).toContainText('instala la versión 0.3.05');
    await expect(screen.locator('li[aria-current="step"]')).toHaveText(/Instalar/);
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: '../../evidence/update-remote-390.png' });

});

test('if the install fails with everyone signed out, the window goes to sign-in and says why', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: true, version: '0.3.4', notes: '', bytes: 10, blocked: '', otherSessions: 0 }, 'updates.prepare': { update: true }, 'updates.install': { error: 'No se pudo abrir el instalador' }, status: { authenticated: false } });
    const dialog = await openVersion(page);
    await dialog.getByRole('button', { name: 'Buscar actualización' }).click();
    await dialog.getByRole('button', { name: 'Actualizar ahora' }).click();
    await expect(page.locator('main')).toHaveCount(0);
    await expect(page.getByText('No se pudo abrir el instalador')).toBeVisible();

});

test('a waiter sees the version but no way to install it', async ({ page }) => {

    await signedInAs(page, 'mesero', {});
    const dialog = await openVersion(page);
    await expect(dialog).toContainText('Un gerente puede buscar actualizaciones');
    await expect(dialog.getByRole('button', { name: 'Buscar actualización' })).toHaveCount(0);

});

test('a published version reaches the manager as a notice that opens the update', async ({ page }) => {

    await signedInAs(page, 'manager', { 'updates.look': { update: null } }, { updateNotice: { version: '0.3.4', notes: 'Corrige la caja' } });
    const notice = page.locator('.update-notice');
    await expect(notice).toContainText('versión 0.3.4');
    await notice.getByRole('button', { name: 'Ver actualización' }).click();
    await expect(page.getByRole('dialog', { name: /^Culinary Restaurant/ })).toBeVisible();
    await page.getByRole('button', { name: 'Cerrar versión' }).click();
    await notice.getByRole('button', { name: 'Cerrar aviso de actualización' }).click();
    await expect(notice).toHaveCount(0);

});

test('after an update the app says what arrived', async ({ page }) => {

    await page.addInitScript(() => { (window as any).arrived = { arrived: true, version: '0.3.4', notes: 'Corrige la caja' }; });
    await kiosk(page);
    const dialog = page.getByRole('dialog', { name: /se actualizó a 0\.3\.04/ });
    await expect(dialog).toContainText('Corrige la caja');

});

test('a failure in the window reaches the bridge with the screen and the role, never who', async ({ page }) => {

    await kiosk(page);
    await page.evaluate(() => { const w = window as any, invoke = w.__TAURI_INTERNALS__.invoke; w.reports = []; w.__TAURI_INTERNALS__.invoke = (name: string, p: any) => { if (p.command === 'fault.report') w.reports.push(p.args); return invoke(name, p); }; });
    await page.evaluate(() => { setTimeout(() => { throw new Error('fallo de prueba'); }); });
    await expect.poll(() => page.evaluate(() => (window as any).reports.length)).toBe(1);
    const report = await page.evaluate(() => (window as any).reports[0]);
    expect(report.what).toContain('fallo de prueba');
    expect(report.affected).toContain('la pantalla overview');
    expect(report.context).toMatchObject({ page: 'overview', role: 'manager' });
    expect(JSON.stringify(report)).not.toContain('Pizza de Peperoni');
    // A failure that repeats is one report, not one per frame.
    await page.evaluate(() => { setTimeout(() => { throw new Error('fallo de prueba'); }); });
    await page.waitForTimeout(300);
    expect(await page.evaluate(() => (window as any).reports.length)).toBe(1);

});

test('a screen that cannot draw is replaced by a way back instead of a blank window', async ({ page }) => {

    await kiosk(page);
    await page.evaluate(() => { const w = window as any, invoke = w.__TAURI_INTERNALS__.invoke; w.reports = []; w.__TAURI_INTERNALS__.invoke = (name: string, p: any) => { if (p.command === 'fault.report') w.reports.push(p.args); if (p.command === 'state') return Promise.resolve({ ok: true, data: { user: { role: 'manager', name: 'x' }, restaurant: { name: 'x', modules: [], settings: { color: '#000', stations: [] } }, branches: 'roto', stateVersion: 'otra' } }); return invoke(name, p); }; });
    await expect(page.locator('.fault-screen')).toContainText('Algo falló en esta pantalla', { timeout: 15000 });
    await expect(page.getByRole('button', { name: 'Recargar la aplicación' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => (window as any).reports.at(-1)?.what || '')).toContain('La interfaz dejó de dibujarse');

});

test('on battery the top bar shows charge and service time; on mains it shows nothing', async ({ page }) => {

    await kiosk(page, { available: true, onBattery: true, source: 'battery', percent: 64, seconds: 7800 });
    const chip = page.getByRole('status', { name: /Con pila, sin cable · 64 % · unas 2 h 10 min de servicio/ });
    await expect(chip).toHaveText('64 %≈ 2 h 10 min');
    await page.screenshot({ path: '../../evidence/kiosk-battery.png' });

});

test('a UPS without estimate shows only the charge and a low level is marked', async ({ page }) => {

    await kiosk(page, { available: true, onBattery: true, source: 'ups', percent: 12, seconds: null, low: true });
    const chip = page.getByRole('status', { name: 'Con UPS, sin corriente · 12 %' });
    await expect(chip).toHaveText('12 %');
    await expect(chip).toHaveClass(/low/);

});

test('mains power and machines without battery data show no indicator', async ({ page }) => {

    await kiosk(page);
    await expect(page.locator('.network-trigger')).toBeVisible();
    await expect(page.locator('.battery-status')).toHaveCount(0);
    await page.evaluate(() => { (window as any).battery = { available: false }; });
    await expect(page.locator('.battery-status')).toHaveCount(0);

});

test('selectable text and fields keep their browser shortcuts', async ({ page, context }) => {

    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await kiosk(page);
    expect(await page.evaluate(() => getComputedStyle(document.querySelector('main h1')!).userSelect)).not.toBe('none');
    const prevented = await page.evaluate(() => { const seen: string[] = []; document.addEventListener('keydown', e => { setTimeout(() => { if (e.defaultPrevented) seen.push(e.key); }); }); (window as any).prevented = seen; return seen; });
    expect(prevented).toEqual([]);
    for (const key of ['a', 'c', 'v']) await page.keyboard.press('Control+' + key);
    await expect(page.locator('main h1')).toBeVisible();
    expect(await page.evaluate(() => (window as any).prevented)).toEqual([]);
    expect(await page.evaluate(() => window.getSelection()?.toString().length)).toBeGreaterThan(0);

});

test('the manager sets the time zone and the end of the business day', async ({ page }) => {

    await kiosk(page);
    await page.evaluate(() => { const w = window as any, invoke = w.__TAURI_INTERNALS__.invoke; w.saved = []; w.__TAURI_INTERNALS__.invoke = (name: string, p: any) => { if (JSON.stringify(p).includes('settings.save')) w.saved.push(p); return invoke(name, p); }; });
    await page.getByText('Restaurante', { exact: true }).first().click();
    await page.getByRole('button', { name: 'Configuración', exact: true }).click();
    await page.getByRole('button', { name: 'Editar configuración' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('Zona horaria').selectOption('America/Tijuana');
    await expect(dialog.getByLabel('El día de ventas termina a las')).toHaveValue('240');
    await dialog.getByLabel('El día de ventas termina a las').selectOption('300');
    await dialog.getByRole('button', { name: /Guardar/ }).click();
    await expect.poll(() => page.evaluate(() => JSON.stringify((window as any).saved))).toContain('"timeZone":"America/Tijuana"');
    expect(await page.evaluate(() => JSON.stringify((window as any).saved))).toContain('"businessDayCutoffMinutes":300');

});
