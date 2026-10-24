// Human name for a visit: its table, or the pickup/delivery customer when it has no table.
export function visitLabel(s: any, v: any): string {

    if (!v) return 'Servicio';
    const table = (s?.tables || []).find((t: any) => t.id === v.tableId);
    if (table) return table.name;
    if (v.tableId) return 'Mesa';
    const delivery = (s?.deliverys || []).find((d: any) => d.visitId === v.id);
    const customer = (s?.customers || []).find((c: any) => c.id === (delivery?.customerId || v.customerId));
    if (delivery) return (delivery.mode === 'pickup' ? 'Para llevar' : 'Delivery') + (customer ? ' · ' + customer.name : '');
    return customer ? 'Pedido · ' + customer.name : 'Servicio rápido';

}
