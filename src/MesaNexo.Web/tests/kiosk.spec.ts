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
