type ProductOptions = {id?:string;name?:string;supportsCooking?:boolean};

export const cookingLevels = ['Medio', 'Tres cuartos', 'Bien cocido'];

export function supportsCooking(product:ProductOptions) {
  if (typeof product.supportsCooking === 'boolean') return product.supportsCooking;
  // Preserve the original demo hamburger when opening a database made before
  // this setting existed. Other products need an explicit catalog setting.
  return /^(r1|r2)-p1$/.test(product.id || '') && product.name === 'Hamburguesa de la casa';
}
