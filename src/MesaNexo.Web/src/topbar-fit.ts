// Callback ref for .topbar-in. The real widths depend on the role, the restaurant's name and the
// kiosk controls, so a fixed breakpoint cannot say when the row fits: measuring it can.
export function fitTopbar(bar: HTMLDivElement) {

    const measure = () => {

        const style = getComputedStyle(bar), items = Array.from(bar.children) as HTMLElement[];
        const room = bar.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
        const needed = items.reduce((total, item) => total + item.offsetWidth, parseFloat(style.columnGap) * (items.length - 1));
        bar.classList.toggle('is-stacked', needed > room);

    };
    const observer = new ResizeObserver(measure);
    observer.observe(bar);
    for (const item of Array.from(bar.children)) observer.observe(item);
    return () => observer.disconnect();

}
