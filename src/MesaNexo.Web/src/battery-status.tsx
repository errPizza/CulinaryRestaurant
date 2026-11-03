import React, { useEffect, useState } from 'react';
import { native } from './bridge';
import { UiIcon } from './ui';

type Battery = { available: boolean; onBattery?: boolean; source?: 'battery' | 'ups'; percent?: number | null; seconds?: number | null; low?: boolean };
const LOW_PERCENT = 20;
export function serviceTime(seconds: number) {

    const minutes = Math.max(1, Math.round(seconds / 60)), hours = Math.floor(minutes / 60);
    return hours ? hours + ' h' + (minutes % 60 ? ' ' + minutes % 60 + ' min' : '') : minutes + ' min';

}

// Shown only while the kiosk runs on its battery or UPS, with what the hardware reports and nothing estimated here.
export function BatteryStatus() {

    const [status, setStatus] = useState<Battery>({ available: false });
    useEffect(() => {

        const refresh = () => native<Battery>('power.battery').then(setStatus).catch(() => {});
        void refresh();
        const timer = window.setInterval(refresh, 30000);
        return () => window.clearInterval(timer);

    }, []);
    if (!status.available || !status.onBattery) return null;
    const { percent, seconds } = status, time = seconds ? serviceTime(seconds) : '';
    const low = status.low || (percent != null && percent <= LOW_PERCENT);
    const source = status.source === 'ups' ? 'Con UPS, sin corriente' : 'Con pila, sin cable';
    const label = [source, percent != null ? percent + ' %' : '', time ? 'unas ' + time + ' de servicio' : ''].filter(Boolean).join(' · ');
    return <span className={'battery-status' + (low ? ' low' : '')} role="status" aria-label={label}>
        <UiIcon name="battery" />
        {percent != null && <span>{percent} %</span>}
        {time && <span className="battery-time">≈ {time}</span>}
    </span>;

}
