import { test, expect } from '@playwright/test';

test('kiosk paints before ready and does not wait for vault unlock', async ({ page }) => {

    await page.addInitScript(() => {

        const w = window as any;
        w.kioskCalls = [];
        w.__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command === 'window.present') {

                    w.kioskCalls.push({ name: 'present', time: performance.now(), login: !!document.querySelector('input') });
                    return { ok: true, data: { enabled: true } };

                }
                if (p.command === 'window.ready') {

                    w.kioskCalls.push({ name: 'ready', time: performance.now(), login: !!document.querySelector('input') });
                    return { ok: true, data: { visible: true } };

                }
                // Simulate Secret Service waiting for its native unlock dialog.
                if (p.command === 'profile.get') return new Promise(() => {});
                return { ok: true, data: {} };

            }
        };

    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Inicia sesión' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => (window as any).kioskCalls.length)).toBe(2);
    const calls = await page.evaluate(() => (window as any).kioskCalls);
    expect(calls.map((x: any) => x.name)).toEqual(['present', 'ready']);
    expect(calls.every((x: any) => x.login)).toBe(true);
    expect(calls[1].time - calls[0].time).toBeGreaterThanOrEqual(175);
    await expect(page.getByRole('button', { name: /Cargando conexión/ })).toBeDisabled();

});

test('ordinary desktop does not start the kiosk heartbeat', async ({ page }) => {

    await page.addInitScript(() => {

        const w = window as any;
        w.kioskCalls = [];
        w.__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command.startsWith('window.')) w.kioskCalls.push(p.command);
                return { ok: true, data: p.command === 'window.present' ? { enabled: false } : p.command === 'profile.get' ? null : p.command === 'motion.get' ? { reduced: true } : { authenticated: false } };

            }
        };

    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Inicia sesión' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => (window as any).kioskCalls)).toEqual(['window.present']);

});


test('kiosk login receives real pointer and keyboard input after empty-form validation', async ({ page }) => {

    await page.addInitScript(() => {

        (window as any).__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command === 'window.present') return { ok: true, data: { enabled: true } };
                if (p.command === 'window.ready') return { ok: true, data: { visible: true } };
                if (p.command === 'profile.get') return { ok: true, data: { ServerUrl: 'https://restaurant.invalid:8443' } };
                return { ok: true, data: p.command === 'motion.get' ? { reduced: false } : { authenticated: false } };

            }
        };

    });
    await page.goto('/');
    const user = page.getByLabel('Usuario', { exact: true });
    const password = page.getByLabel('Contraseña', { exact: true });
    const code = page.getByLabel('Código de alta del dispositivo (sólo primer acceso)', { exact: true });
    await page.getByRole('button', { name: /Entrar a la operación/ }).click();
    await user.click();
    await expect(user).toBeFocused();
    await page.keyboard.type('operador-de-prueba');
    await page.keyboard.press('Tab');
    await expect(password).toBeFocused();
    await page.keyboard.type('Solo-prueba-1234');
    await code.click();
    await expect(code).toBeFocused();
    await page.keyboard.type('codigo-de-prueba');
    await expect(user).toHaveValue('operador-de-prueba');
    await expect(password).toHaveValue('Solo-prueba-1234');
    await expect(code).toHaveValue('codigo-de-prueba');
    await page.getByRole('button', { name: 'Mostrar contraseña', exact: true }).click();
    await expect(password).toHaveAttribute('type', 'text');
    await expect(password).toHaveValue('Solo-prueba-1234');

});

test('kiosk login offers suspend, restart and confirmed power-off', async ({ page }) => {

    await page.addInitScript(() => {

        const w = window as any;
        w.powerCalls = [];
        w.__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command === 'window.present') return { ok: true, data: { enabled: true } };
                if (p.command === 'window.ready') return { ok: true, data: { visible: true } };
                if (p.command === 'power.status') return { ok: true, data: { available: true } };
                if (p.command === 'power.request') { w.powerCalls.push(p.args.action); return { ok: true, data: { action: p.args.action } }; }
                if (p.command === 'profile.get') return { ok: true, data: { ServerUrl: 'https://restaurant.invalid:8443' } };
                return { ok: true, data: p.command === 'motion.get' ? { reduced: true } : { authenticated: false } };

            }
        };

    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Inicia sesión' })).toBeVisible();
    const power = page.getByRole('button', { name: 'Energía del equipo' });
    await power.click();
    await expect(page.getByRole('menuitem', { name: /Suspender/ })).toBeVisible();
    await expect(page.getByRole('menuitem', { name: /Reiniciar/ })).toBeVisible();
    await page.screenshot({ path: '../../evidence/kiosk-power-login.png' });
    await page.getByRole('menuitem', { name: /Apagar/ }).click();
    await expect(page.getByRole('menuitem', { name: /Apagar ahora/ })).toBeVisible();
    expect(await page.evaluate(() => (window as any).powerCalls)).toEqual([]);
    await page.getByRole('menuitem', { name: /Apagar ahora/ }).click();
    await expect.poll(() => page.evaluate(() => (window as any).powerCalls)).toEqual(['poweroff']);

});

test('power controls stay hidden outside the appliance', async ({ page }) => {

    await page.addInitScript(() => {

        (window as any).__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => ({ ok: true, data: p.command === 'window.present' ? { enabled: false } : p.command === 'power.status' ? { available: false } : p.command === 'profile.get' ? null : p.command === 'motion.get' ? { reduced: true } : { authenticated: false } })
        };

    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Inicia sesión' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Energía del equipo' })).toHaveCount(0);

});

test('kiosk shows the wired link and joins open and protected Wi-Fi networks', async ({ page }) => {

    await page.addInitScript(() => {

        const w = window as any;
        w.connects = [];
        w.netState = { available: true, kind: 'ethernet', name: 'Cable de red', signal: null, wifi: true };
        w.__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command === 'window.present') return { ok: true, data: { enabled: true } };
                if (p.command === 'window.ready') return { ok: true, data: { visible: true } };
                if (p.command === 'power.status') return { ok: true, data: { available: true } };
                if (p.command === 'network.status') return { ok: true, data: w.netState };
                if (p.command === 'network.scan') return { ok: true, data: { networks: [
                    { ssid: 'Terraza', signal: 82, secure: true, active: false },
                    { ssid: 'Invitados', signal: 40, secure: false, active: false },
                ] } };
                if (p.command === 'network.connect') {

                    w.connects.push(p.args);
                    if (p.args.ssid === 'Terraza' && p.args.password !== 'clave-segura') return { ok: false, error: 'La contraseña no es correcta.' };
                    w.netState = { available: true, kind: 'wifi', name: p.args.ssid, signal: 82, wifi: true };
                    return { ok: true, data: { ssid: p.args.ssid } };

                }
                if (p.command === 'profile.get') return { ok: true, data: { ServerUrl: 'https://restaurant.invalid:8443' } };
                return { ok: true, data: p.command === 'motion.get' ? { reduced: true } : { authenticated: false } };

            }
        };

    });
    await page.goto('/');
    const trigger = page.getByRole('button', { name: /Red del equipo: Conectado por cable/ });
    await expect(trigger).toBeVisible();
    await trigger.click();
    const panel = page.getByRole('dialog', { name: 'Red del equipo' });
    await expect(panel.getByRole('button', { name: /Terraza/ })).toBeVisible();
    await page.screenshot({ path: '../../evidence/kiosk-network-login.png', animations: 'disabled' });

    await panel.getByRole('button', { name: /Invitados/ }).click();
    await expect.poll(() => page.evaluate(() => (window as any).connects)).toEqual([{ ssid: 'Invitados', password: '' }]);
    await expect(page.getByRole('button', { name: /Red del equipo: Wi‑Fi · Invitados/ })).toBeVisible();

    await panel.getByRole('button', { name: /Terraza/ }).click();
    await panel.getByLabel('Contraseña de Terraza').fill('incorrecta');
    await panel.getByRole('button', { name: 'Conectar' }).click();
    await expect(panel.getByRole('alert')).toHaveText('La contraseña no es correcta.');
    await panel.getByLabel('Contraseña de Terraza').fill('clave-segura');
    await panel.getByRole('button', { name: 'Conectar' }).click();
    await expect(page.getByRole('button', { name: /Red del equipo: Wi‑Fi · Terraza/ })).toBeVisible();

});

const signedIn = { user: { id: 'u1', name: 'Pizza de Peperoni', role: 'gerente' }, restaurant: { name: 'McMamadas', branchId: 'b1', modules: [] }, branches: [{ id: 'b1', name: 'McMamadas' }] };
async function kioskWithSource(page: import('@playwright/test').Page, source: object) {

    await page.addInitScript(({ source, signedIn }) => {

        (window as any).__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => {

                if (p.command === 'window.present') return { ok: true, data: { enabled: true } };
                if (p.command === 'window.ready') return { ok: true, data: { visible: true } };
                if (p.command === 'power.status') return { ok: true, data: { available: true } };
                if (p.command === 'power.source') return { ok: true, data: source };
                if (p.command === 'network.status') return { ok: true, data: { available: true, kind: 'ethernet', name: 'Cable de red', signal: null, wifi: true } };
                if (p.command === 'profile.get') return { ok: true, data: { ServerUrl: 'https://restaurant.invalid:8443' } };
                if (p.command === 'motion.get') return { ok: true, data: { reduced: true } };
                if (p.command === 'license.status') return { ok: true, data: { state: 'active' } };
                if (p.command === 'status') return { ok: true, data: { authenticated: true } };
                if (p.command === 'state') return { ok: true, data: signedIn };
                return { ok: true, data: {} };

            }
        };

    }, { source, signedIn });
    await page.goto('/');

}
test.describe('topbar', () => {

    for (const [source, name] of [[{ onBattery: true, source: 'laptop', percent: 78, minutes: 125 }, /Funcionando con pila: 78 %, quedan cerca de 2 h 5 min/], [{ onBattery: true, source: 'ups', percent: 15, minutes: null }, /Funcionando con UPS: 15 %/]] as const) test('shows the charge left: ' + source.source, async ({ page }) => {

        await kioskWithSource(page, source);
        await expect(page.getByRole('status', { name })).toBeVisible();

    });
    test('stays out of the way on mains power', async ({ page }) => {

        await kioskWithSource(page, { onBattery: false });
        await expect(page.locator('.topbar')).toBeVisible();
        await expect(page.locator('.battery-status')).toHaveCount(0);

    });
    for (const width of [1280, 1366, 1440, 1536, 1600, 1640, 1700, 1760, 1920, 2560]) test('fills ' + width + ' px without overlapping, even on battery', async ({ page }) => {

        await page.setViewportSize({ width, height: 800 });
        await kioskWithSource(page, { onBattery: true, source: 'laptop', percent: 78, minutes: 125 });
        await expect(page.locator('.topbar')).toBeVisible();
        const boxes = await page.locator('.topbar-in > *').evaluateAll(items => items.map(item => { const box = item.getBoundingClientRect(); return { left: box.left, right: box.right, top: box.top, bottom: box.bottom }; }));
        for (const [i, a] of boxes.entries()) for (const b of boxes.slice(i + 1)) expect(a.right <= b.left || b.right <= a.left || a.bottom <= b.top || b.bottom <= a.top).toBe(true);
        expect(Math.max(...boxes.map(box => box.right))).toBeLessThanOrEqual(width);
        const navigation = page.getByRole('navigation', { name: 'Navegación principal' });
        expect(await navigation.evaluate(nav => nav.scrollWidth <= nav.clientWidth)).toBe(true);
        expect(await page.locator('.main').evaluate(main => main.getBoundingClientRect().width)).toBe(width);
        await page.screenshot({ path: `../../evidence/hardware-topbar-${width}.png`, animations: 'disabled' });

    });

});

test('network controls stay hidden outside the appliance', async ({ page }) => {

    await page.addInitScript(() => {

        (window as any).__TAURI_INTERNALS__ = {
            invoke: async (_name: string, p: any) => ({ ok: true, data: p.command === 'window.present' ? { enabled: false } : p.command === 'network.status' ? { available: false } : p.command === 'profile.get' ? null : p.command === 'motion.get' ? { reduced: true } : { authenticated: false } })
        };

    });
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Inicia sesión' })).toBeVisible();
    await expect(page.getByRole('button', { name: /Red del equipo/ })).toHaveCount(0);

});
