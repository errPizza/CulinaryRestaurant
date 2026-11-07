import { test, expect, Page } from '@playwright/test';

// A manager's session whose state the test changes between polls; every tone that plays lands in __culinarySounds.
const base = () => ({
    siteId: 'local', user: { id: 'u1', name: 'Gerencia', role: 'manager', permissions: {} },
    restaurant: { name: 'Sucursal', branchId: 'b1', revision: 1, modules: [], settings: { color: '#f2b93b', stations: ['Cocina'], delayMinutes: 15, discountLimit: 20 } },
    branches: [{ id: 'b1', name: 'Sucursal' }], tables: [], visits: [{ id: 'v1', branchId: 'b1', waiterId: 'u1', status: 'open' }], units: [], commands: [{ id: 'c0', unitIds: [], at: new Date().toISOString() }], notifications: [], users: [], products: [], categorys: [],
    cashs: [], requests: [], movements: [], audits: [], devices: [], sessions: [], folios: [], payments: [], connection: { central: 'pending' },
    calls: [{ id: 'old-call', at: new Date().toISOString(), attended: false }], aggregatororders: [], terminaltxns: [], paylinks: [], printjobs: [], tickets: [],
});
async function open(page: Page) {

    await page.addInitScript(fixture => {

        const w = window as any;
        w.fixture = fixture;
        w.version = 1;
        w.__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command === 'state') return { ok: true, data: { ...w.fixture, stateVersion: String(w.version) } };
                const data: Record<string, any> = { 'profile.get': { ServerUrl: 'https://restaurant.invalid:8443' }, 'motion.get': { reduced: true }, 'theme.get': { theme: 'light' }, status: { authenticated: true }, 'license.status': { state: 'active' }, 'updates.arrived': { arrived: false } };
                return { ok: true, data: data[p.command] ?? {} };

            }
        };

    }, base());
    await page.goto('/');
    await expect(page.locator('main h1')).toBeVisible();

}
const played = (page: Page) => page.evaluate(() => ((window as any).__culinarySounds || []).map((x: any) => x.event));
const change = (page: Page, fn: string) => page.evaluate(code => { const w = window as any; new Function('s', code)(w.fixture); w.version++; }, fn);

test('nothing plays for what was already there when the app opened, and each new event plays its tone', async ({ page }) => {

    await open(page);
    await page.waitForTimeout(2500);
    expect(await played(page)).toEqual([]);
    const steps: [string, string][] = [
        ["s.aggregatororders.push({ id: 'o1', status: 'pending' })", 'order'],
        ["s.commands.push({ id: 'c1', unitIds: [], at: new Date().toISOString() })", 'ticket'],
        ["s.units.push({ id: 'x1', visitId: 'v1', status: 'ready' })", 'ready'],
        ["s.terminaltxns.push({ id: 't1', actor: 'u1', status: 'approved' })", 'approved'],
        ["s.terminaltxns.push({ id: 't2', actor: 'u1', status: 'declined' })", 'declined'],
        ["s.paylinks.push({ id: 'l1', actor: 'u1', status: 'paid' })", 'approved'],
        ["s.tickets.push({ id: 'k1', channel: 'owner', messages: [{ id: 'm1', actor: 'owner', name: 'Dueño', at: new Date().toISOString() }] })", 'message'],
        ["s.printjobs.push({ id: 'p1', status: 'failed' })", 'error'],
        ["s.updateNotice = { version: '0.3.5' }", 'alert'],
    ];
    for (const [code, event] of steps) {

        const before = (await played(page)).length;
        await change(page, code);
        await expect.poll(async () => (await played(page)).slice(before)).toEqual([event]);

    }

});

test('an incoming call rings until it is answered', async ({ page }) => {

    await open(page);
    await page.waitForTimeout(2500);
    await change(page, "s.calls.push({ id: 'call1', at: new Date().toISOString(), attended: false })");
    await expect.poll(async () => (await played(page)).filter((e: string) => e === 'call').length, { timeout: 9000 }).toBeGreaterThanOrEqual(3);
    await change(page, "s.calls.forEach(c => c.attended = true)");
    await page.waitForTimeout(2500);
    const rings = (await played(page)).length;
    await page.waitForTimeout(4500);
    expect((await played(page)).length).toBe(rings);

});

test('the device setting persists, «Bajo» keeps errors at a medium level and «Probar» plays every tone', async ({ page }) => {

    await open(page);
    await page.getByRole('button', { name: /^Menú de/ }).click();
    const group = page.getByRole('group', { name: 'Sonidos' });
    await expect(group.getByRole('button')).toHaveText(['Sonido', 'Bajo', 'Silencio']);
    await expect(group.getByRole('button', { name: 'Sonido' })).toHaveAttribute('aria-pressed', 'true');
    await group.getByRole('button', { name: 'Bajo' }).click();
    await page.reload();
    await expect(page.locator('main h1')).toBeVisible();
    await page.getByRole('button', { name: /^Menú de/ }).click();
    await expect(page.getByRole('group', { name: 'Sonidos' }).getByRole('button', { name: 'Bajo' })).toHaveAttribute('aria-pressed', 'true');
    await page.getByRole('button', { name: 'Probar' }).click();
    await expect.poll(async () => page.evaluate(() => (window as any).__culinarySounds.length), { timeout: 15000 }).toBe(9);
    const log = await page.evaluate(() => (window as any).__culinarySounds.map((x: any) => [x.event, x.volume]));
    expect(log.map((x: any) => x[0])).toEqual(['order', 'ticket', 'ready', 'approved', 'declined', 'call', 'message', 'error', 'alert']);
    for (const [event, volume] of log) expect(volume).toBe(event === 'error' || event === 'declined' ? 0.32 : 0.15);
    await page.getByRole('group', { name: 'Sonidos' }).getByRole('button', { name: 'Silencio' }).click();
    await expect(page.getByRole('button', { name: 'Probar' })).toBeDisabled();
    await change(page, "s.aggregatororders.push({ id: 'o9', status: 'pending' })");
    await page.waitForTimeout(3000);
    expect(await page.evaluate(() => (window as any).__culinarySounds.length)).toBe(9);

});

test.describe('on Android', () => {

    test.use({ userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36' });
    test('«Solo vibrar» appears and vibrates without sound', async ({ page }) => {

        await page.addInitScript(() => { const w = window as any; w.vibrated = []; Object.defineProperty(Navigator.prototype, 'vibrate', { configurable: true, value: (p: number[]) => { w.vibrated.push(p); return true; } }); });
        await open(page);
        await page.getByRole('button', { name: /^Menú de/ }).click();
        const group = page.getByRole('group', { name: 'Sonidos' });
        await expect(group.getByRole('button')).toHaveText(['Sonido', 'Bajo', 'Solo vibrar', 'Silencio']);
        await group.getByRole('button', { name: 'Solo vibrar' }).click();
        await page.keyboard.press('Escape');
        await page.waitForTimeout(2500);
        const before = await page.evaluate(() => (window as any).vibrated.length);
        await change(page, "s.terminaltxns.push({ id: 't9', actor: 'u1', status: 'declined' })");
        await expect.poll(async () => page.evaluate(() => (window as any).vibrated.length)).toBe(before + 1);
        const last = await page.evaluate(() => { const w = window as any; return { pattern: w.vibrated.at(-1), entry: w.__culinarySounds.at(-1) }; });
        expect(last.pattern).toEqual([450]);
        expect(last.entry.event).toBe('declined');
        expect(last.entry.volume).toBe(0);

    });

});
