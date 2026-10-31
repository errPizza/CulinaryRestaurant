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
                    'motion.get': { reduced: false }, 'theme.get': { theme: 'light' }, status: { authenticated: true }, state: fixture, 'license.status': { state: 'active' },
                };
                return { ok: true, data: data[p.command] ?? {} };

            }
        };

    }, [state, battery] as const);
    await page.goto('/');
    await expect(page.locator('main h1')).toBeVisible();

}
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
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
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

test('changing page is one plain fade with no blur, movement or delayed blocks', async ({ page }) => {

    await kiosk(page, undefined, 1366, 768);
    await page.getByRole('button', { name: 'Bandeja de pendientes' }).click();
    const animations = await page.evaluate(() => document.getAnimations().map(a => {

        const effect = a.effect as KeyframeEffect;
        return { name: (a as CSSAnimation).animationName, delay: effect.getTiming().delay, target: (effect.target as Element)?.className, props: effect.getKeyframes().flatMap(k => Object.keys(k)) };

    }).filter(a => ['H1', 'page-content'].includes(a.target as string) || a.name === 'fade'));
    expect(animations).toEqual([expect.objectContaining({ name: 'fade', target: 'page-content', delay: 0 })]);
    expect(animations[0].props).not.toContain('filter');
    expect(animations[0].props).not.toContain('transform');
    const css = await page.evaluate(() => [...document.styleSheets].flatMap(s => [...s.cssRules]).map(r => r.cssText).filter(t => /filter:\s*blur/.test(t) && /page-content|stagger|h1/.test(t)));
    expect(css).toEqual([]);

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
