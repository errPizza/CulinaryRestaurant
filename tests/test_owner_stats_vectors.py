"""Recalculates the owner panel vector from its raw records, independently of the hand-written expected values, so a
wrong definition or a typo in tests/vectors/owner-stats fails here before OwnerStats and the server build on it."""
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from decimal import Decimal, ROUND_HALF_UP
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
VECTOR = json.loads((ROOT / 'tests/vectors/owner-stats/v1.json').read_text(encoding='utf-8'))
HOUR_KEYS = ('tickets', 'people', 'gross', 'discounts', 'net', 'tax', 'deliveryFees', 'voidedCount', 'voidedAmount', 'comps', 'staffMeals')
VOID = ('cancelled', 'unavailable')


def check(condition, name):
    if not condition:
        raise AssertionError(name)
    print('PASS', name)


def rounded(value):
    return int(Decimal(value).quantize(Decimal(1), rounding=ROUND_HALF_UP))


def allocate(amount, weights):
    """Cents.Allocate: floors, then hands the residue to the largest remainders."""
    total = sum(weights)
    if total == 0:
        return [0] * len(weights)
    raw = [Decimal(amount) * weight / total for weight in weights]
    result = [int(x) for x in raw]
    order = sorted(range(len(weights)), key=lambda i: (result[i] - raw[i], i))
    for i in order[:amount - sum(result)]:
        result[i] += 1
    return result


def moment(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00'))


def local(value):
    return moment(value).astimezone(timezone(timedelta(minutes=VECTOR['branch']['utcOffsetMinutes'])))


def business(value):
    at = local(value)
    return (at - timedelta(minutes=VECTOR['branch']['businessDayCutoffMinutes'])).date().isoformat(), at.hour


def totals(visit):
    all_units = sorted((u for u in VECTOR['units'] if u['visitId'] == visit['id']), key=lambda u: u['id'])
    sold = [u for u in all_units if u['status'] not in VOID]
    gross = sum(u['price'] for u in sold) + visit['deliveryFee']
    discount = visit['discount']
    if discount.get('kind') == 'percent':
        cut = rounded(Decimal(gross) * discount['value'] / 100)
    else:
        cut = min(gross, discount.get('value', 0))
    net = allocate(gross - cut, [u['price'] for u in sold] + [visit['deliveryFee']])
    tax = sum(rounded(Decimal(net[i]) * u['taxBps'] / (10000 + u['taxBps'])) for i, u in enumerate(sold))
    voided = [u for u in all_units if u['status'] in VOID]
    return {'tickets': 1, 'people': visit['people'], 'gross': gross, 'discounts': cut, 'net': gross - cut, 'tax': tax, 'deliveryFees': visit['deliveryFee'], 'voidedCount': len(voided), 'voidedAmount': sum(u['price'] for u in voided), 'comps': int(discount.get('kind') == 'percent' and discount['value'] == 100), 'staffMeals': int(visit['channel'] == 'staff')}


def covering_z(at):
    """The Z whose window holds a moment; the next one while it has not been taken; 0 before the first."""
    reports = VECTOR['zReports']
    if moment(at) < moment(reports[0]['from']):
        return 0
    for report in reports:
        if moment(report['from']) <= moment(at) < moment(report['to']):
            return report['number']
    return reports[-1]['number'] + 1


def build_day(date):
    visits = [v for v in VECTOR['visits'] if business(v['closedAt'])[0] == date]
    hours = defaultdict(lambda: dict.fromkeys(HOUR_KEYS, 0))
    channels, staff, products = defaultdict(lambda: [0, 0]), defaultdict(lambda: [0, 0, 0]), defaultdict(lambda: [0, 0])
    for visit in visits:
        row = hours[(business(visit['closedAt'])[1], covering_z(visit['closedAt']))]
        amounts = totals(visit)
        for key in HOUR_KEYS:
            row[key] += amounts[key]
        channel = channels[visit['channel'] or 'table']
        channel[0] += 1
        channel[1] += amounts['net']
        person = staff[visit['waiterId']]
        person[0] += 1
        person[1] += amounts['net']
        for unit in VECTOR['units']:
            if unit['visitId'] == visit['id'] and unit['status'] not in VOID:
                products[unit['productId']][0] += 1
                products[unit['productId']][1] += unit['price']
    payments = defaultdict(lambda: [0, 0, 0])
    for pay in VECTOR['payments']:
        day, hour = business(pay['at'])
        if day == date:
            row = payments[(hour, pay['method'])]
            row[0] += pay['amount']
            row[1] += pay['tip']
            row[2] += 1
            if pay['tip']:
                staff[next(v['waiterId'] for v in VECTOR['visits'] if v['id'] == pay['visitId'])][2] += pay['tip']
    refunds = defaultdict(lambda: [0, 0])
    for refund in VECTOR['refunds']:
        if business(refund['at'])[0] == date:
            refunds[refund['method']][0] += refund['amount']
            refunds[refund['method']][1] += 1
    moved = [m for m in VECTOR['movements'] if business(m['at'])[0] == date]
    stock = [m for m in VECTOR['stockMovements'] if business(m['at'])[0] == date]
    net, tickets, people = (sum(h[key] for h in hours.values()) for key in ('net', 'tickets', 'people'))
    return {
        'businessDate': date,
        'hours': [{'hour': hour, 'z': z, **hours[(hour, z)]} for hour, z in sorted(hours)],
        'payments': [{'hour': hour, 'method': method, 'amount': v[0], 'tips': v[1], 'count': v[2]} for (hour, method), v in sorted(payments.items())],
        'refunds': [{'method': method, 'amount': v[0], 'count': v[1]} for method, v in sorted(refunds.items())],
        'channels': [{'channel': name, 'tickets': v[0], 'net': v[1]} for name, v in sorted(channels.items())],
        'products': [{'productId': key, **{k: VECTOR['products'][key][k] for k in ('name', 'category')}, 'quantity': v[0], 'revenue': v[1], 'unitCost': VECTOR['products'][key]['unitCost']} for key, v in sorted(products.items())],
        'staff': [{'staffId': key, 'name': VECTOR['staff'][key], 'tickets': v[0], 'net': v[1], 'tips': v[2]} for key, v in sorted(staff.items())],
        'cash': {kind: sum(m['amount'] for m in moved if m['type'] == kind) for kind in ('income', 'expense', 'withdrawal')},
        'cashCloses': [{'cashId': c['cashId'], 'closedAt': c['closedAt'], 'blind': c['blind'], 'cashDifference': c['counted'] - c['expected'], 'cardDifference': c['cardCounted'] - c['cardExpected'], 'transferDifference': c['transferCounted'] - c['transferExpected'], 'adjustments': 0} for c in VECTOR['cashCloses'] if business(c['closedAt'])[0] == date],
        'inventory': {name: -sum(m['cost'] for m in stock if m['type'] == kind) for name, kind in (('waste', 'waste'), ('shortage', 'count-shortage'))} | {'surplus': sum(m['cost'] for m in stock if m['type'] == 'count-surplus')},
        'summary': {'tickets': tickets, 'net': net, 'averageTicket': net // tickets if tickets else 0, 'averagePerson': net // people if people else 0}
    }


def frozen_z(report):
    """What ShiftSummary froze: a reopened visit still counted by the close it had when the Z was taken."""
    def closed_then(visit):
        return visit.get('firstClosedAt') if report['number'] == 1 and visit.get('firstClosedAt') else visit['closedAt']
    inside = lambda at: moment(report['from']) <= moment(at) < moment(report['to'])
    visits = [v for v in VECTOR['visits'] if inside(closed_then(v))]
    amounts = [totals(v) for v in visits]
    pays = [p for p in VECTOR['payments'] if inside(p['at'])]
    return {'tickets': len(visits), 'people': sum(a['people'] for a in amounts), 'gross': sum(a['gross'] for a in amounts), 'discounts': sum(a['discounts'] for a in amounts), 'net': sum(a['net'] for a in amounts), 'tax': sum(a['tax'] for a in amounts), 'payments': sum(p['amount'] for p in pays), 'tips': sum(p['tip'] for p in pays), 'refunds': sum(r['amount'] for r in VECTOR['refunds'] if inside(r['at'])), 'voidedCount': sum(a['voidedCount'] for a in amounts), 'voidedAmount': sum(a['voidedAmount'] for a in amounts), 'comps': sum(a['comps'] for a in amounts), 'staffMeals': sum(a['staffMeals'] for a in amounts)}


def main():
    days = {day['businessDate']: build_day(day['businessDate']) for day in VECTOR['expected']['days']}
    for expected in VECTOR['expected']['days']:
        date = expected['businessDate']
        for section, value in expected.items():
            check(days[date][section] == value, f'{date}: {section} matches the recalculation')

    for report in VECTOR['zReports']:
        computed = frozen_z(report)
        check(all(report[key] == value for key, value in computed.items()), f"Z{report['number']} frozen figures match the shift summary rules")

    for expected in VECTOR['expected']['reconciliation']:
        rows = [row for day in days.values() for row in day['hours'] if row['z'] == expected['z']]
        report = next(r for r in VECTOR['zReports'] if r['number'] == expected['z'])
        check(sum(r['net'] for r in rows) == expected['panelNet'], f"Z{expected['z']}: panel net")
        check(sum(r['tickets'] for r in rows) == expected['panelTickets'], f"Z{expected['z']}: panel tickets")
        check(report['net'] == expected['frozenNet'] == expected['panelNet'] + expected['reopenedNet'], f"Z{expected['z']}: frozen net equals panel plus reopened")
        check(report['tickets'] == expected['frozenTickets'] == expected['panelTickets'] + expected['reopenedTickets'], f"Z{expected['z']}: frozen tickets equal panel plus reopened")

    for day in days.values():
        hours, name = day['hours'], day['businessDate']
        net = sum(h['net'] for h in hours)
        check(net == sum(c['net'] for c in day['channels']) == sum(s['net'] for s in day['staff']), f'{name}: identity 1, net by hour, channel and staff')
        check(sum(p['revenue'] for p in day['products']) + sum(h['deliveryFees'] for h in hours) == sum(h['gross'] for h in hours), f'{name}: identity 2, products plus delivery equal gross')
        check(all(h['gross'] - h['discounts'] == h['net'] for h in hours), f'{name}: identity 3, gross less discounts')
        check(sum(p['tips'] for p in day['payments']) == sum(s['tips'] for s in day['staff']), f'{name}: identity 5, tips by payment equal tips by staff')


    check(covering_z('2026-10-29T12:00:00Z') == 0, 'a sale before the first Z belongs to no corte')
    check(covering_z('2026-11-01T09:00:00Z') == 3, 'a sale at the end of the last Z waits for the next one')
    check(business('2026-10-31T09:59:00Z') == ('2026-10-30', 3), 'one minute before the cutoff is still the previous day')
    check(business('2026-10-31T10:00:00Z') == ('2026-10-31', 4), 'the cutoff opens the new business day')

    consolidated = VECTOR['expected']['consolidated']
    own = days[consolidated['businessDate']]['summary']
    other = consolidated['otherBranch']
    tickets, net = own['tickets'] + other['tickets'], own['net'] + other['net']
    check((tickets, net) == (consolidated['tickets'], consolidated['net']), 'consolidated adds the branches')
    check(net // tickets == consolidated['averageTicket'] != (own['averageTicket'] + other['net'] // other['tickets']) // 2, 'consolidated average comes from totals, not from averaging averages')


if __name__ == '__main__':
    main()
