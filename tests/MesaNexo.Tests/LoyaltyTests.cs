using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;
// Stage 3 of the ecosystem sheet: price lists, wallet and cashback, employee meals, X/Z closings, ABC and table QR.
static class LoyaltyTests
{
    public static async Task Run(string root,string python,List<JsonObject> evidence)
    {
        void Check(bool ok,string name){if(!ok)throw new Exception(name);evidence.Add(J.O("test",name,"result","passed"));Console.WriteLine("PASS "+name);}
        async Task Deny(Func<Task> action,string name){try{await action();throw new Exception("Expected rejection: "+name);}catch(BusinessException){Check(true,name);}}
        var folder=Path.Combine(root,".lab","loyalty-"+J.Id());var password="Test-"+J.Id();var enrollment=J.Id();
        var runtime=new RuntimeStore(python,Path.Combine(root,"runtime","lab_host.py"),folder,"loyal-site");var service=new BranchService(runtime,"loyal-site",new Passwords());
        try{
        await service.Initialize(J.O("siteId","loyal-site","name","Loyalty","mode","licencia","managerName","Manager","managerUsername","manager","managerPassword",password,"enrollmentCode",enrollment,"modules",Restaurant.Modules));
        var token=(await service.Login(J.O("siteId","loyal-site","deviceId",J.Id(),"deviceSecret",J.Id(),"deviceName","office","username","manager","password",password,"enrollmentCode",enrollment))).S("token");
        async Task<JsonObject> Act(string type,JsonObject p)=>(await service.Action(token,J.O("operationId",J.Id(),"type",type,"payload",p))).Obj("result");
        var ana=await Act("user.save",J.O("name","Ana","username","ana","role","mesero","password",password));var luis=await Act("user.save",J.O("name","Luis","username","luis","role","mesero","password",password));
        var code=J.Id();await service.RotateEnrollment(code);var anaToken=(await service.Login(J.O("siteId","loyal-site","deviceId",J.Id(),"deviceSecret",J.Id(),"deviceName","pos","username","ana","password",password,"enrollmentCode",code))).S("token");
        async Task<JsonObject> Waiter(string type,JsonObject p)=>(await service.Action(anaToken,J.O("operationId",J.Id(),"type",type,"payload",p))).Obj("result");
        async Task Serve(string visitId){foreach(var uid in (await service.Load()).List("unit").Where(x=>x.S("visitId")==visitId&&x.S("status")=="received").Select(x=>x.S("id")).ToArray()){await Act("unit.state",J.O("id",uid,"status","preparing"));await Act("unit.state",J.O("id",uid,"status","ready"));}}
        await Act("catalog.category",J.O("name","Food"));
        var taco=await Act("catalog.product",J.O("name","Taco","price",3000,"taxBps",1600,"category","Food","station","Cocina"));var water=await Act("catalog.product",J.O("name","Agua","price",2000,"taxBps",1600,"category","Food","station","Cocina"));var steak=await Act("catalog.product",J.O("name","Steak","price",25000,"taxBps",1600,"category","Food","station","Cocina"));
        var store=await Act("inventory.warehouse",J.O("name","Cocina","costMethod","average"));var beef=await Act("inventory.ingredient",J.O("name","Res","unit","g"));await Act("inventory.configure",J.O("warehouseId",store.S("id"),"policy","reserve-on-send-consume-on-close"));
        await Act("stock.receive",J.O("warehouseId",store.S("id"),"ingredientId",beef.S("id"),"quantity","10","unit","kg","cost",150000,"reference","F-1"));var recipe=await Act("recipe.save",J.O("name","Steak","yield","1","lines",new[]{J.O("ingredientId",beef.S("id"),"quantity","200","unit","g")}));await Act("recipe.bind",J.O("productId",steak.S("id"),"recipeId",recipe.S("recipeId")));
        var t1=await Act("table.save",J.O("name","Mesa 1","zone","Salón","capacity",4));var t2=await Act("table.save",J.O("name","Mesa 2","zone","Salón","capacity",4));await Act("cash.open",J.O("initial",50000));

        // Price lists by channel, pinned base price and manual lists.
        await Deny(()=>Act("pricelist.save",J.O("name","Mal","adjustBps",-9500,"channels",new[]{"dine-in"},"auto",true,"startHour",0,"endHour",24)),"price list rejects adjustments below −90 %");
        var happy=await Act("pricelist.save",J.O("name","Hora feliz","adjustBps",-2000,"channels",new[]{"dine-in"},"auto",true,"startHour",0,"endHour",24,"prices",new[]{J.O("productId",water.S("id"),"price",1500)}));
        var events=await Act("pricelist.save",J.O("name","Eventos","adjustBps",1000,"channels",new string[0],"auto",false,"startHour",0,"endHour",24));
        var v1=await Act("visit.open",J.O("tableId",t1.S("id"),"people",2,"waiterId",ana.S("id")));await Act("command.send",J.O("visitId",v1.S("id"),"items",new[]{J.O("productId",taco.S("id"),"quantity",1),J.O("productId",water.S("id"),"quantity",1),J.O("productId",steak.S("id"),"quantity",2)}));
        var state=await service.Load();var units=state.List("unit").Where(x=>x.S("visitId")==v1.S("id")).ToArray();
        Check(units.First(x=>x.S("productId")==taco.S("id")).L("price")==2400&&units.First(x=>x.S("productId")==water.S("id")).L("price")==1500&&units.All(x=>x.S("priceListId")==happy.S("id")),"automatic dine-in list applies percent and fixed prices");
        var quick=await Act("visit.quick",new());await Act("command.send",J.O("visitId",quick.S("id"),"items",new[]{J.O("productId",taco.S("id"),"quantity",1)}));
        Check((await service.Load()).List("unit").First(x=>x.S("visitId")==quick.S("id")).L("price")==3000,"counter channel keeps catalog price when no list covers it");
        var v2=await Act("visit.open",J.O("tableId",t2.S("id"),"people",2,"waiterId",ana.S("id")));await Act("visit.pricelist",J.O("id",v2.S("id"),"priceListId","base"));await Act("command.send",J.O("visitId",v2.S("id"),"items",new[]{J.O("productId",taco.S("id"),"quantity",1)}));
        await Act("visit.pricelist",J.O("id",v2.S("id"),"priceListId",events.S("id")));await Act("command.send",J.O("visitId",v2.S("id"),"items",new[]{J.O("productId",taco.S("id"),"quantity",1)}));
        var prices=(await service.Load()).List("unit").Where(x=>x.S("visitId")==v2.S("id")).Select(x=>x.L("price")).OrderBy(x=>x).ToArray();Check(prices.SequenceEqual(new long[]{3000,3300}),"a pinned base price and a manual event list apply to later commands only");
        await Deny(()=>Waiter("visit.pricelist",J.O("id",v2.S("id"),"priceListId","base")),"only a manager changes the price list of an account");

        // Wallet: cashback on close, top-up, redemption and limits.
        await Act("loyalty.configure",J.O("enabled",true,"cashbackBps",500));var mario=await Act("customer.save",J.O("name","Mario","phone","5512345678"));
        var sale=await Act("sale.quick",J.O("method","cash","customerId",mario.S("id"),"items",new[]{J.O("productId",taco.S("id"),"quantity",2)}));await Serve(sale.S("visitId"));await Act("visit.deliverAll",J.O("id",sale.S("visitId")));
        state=await service.Load();Check(state.Get("visit",sale.S("visitId")).S("status")=="closed"&&state.List("walletmove").Single(x=>x.S("customerId")==mario.S("id")).L("amount")==300,"closing a customer's account earns 5 % cashback in the wallet");
        await Act("wallet.topup",J.O("customerId",mario.S("id"),"amount",1000,"method","cash"));
        await Deny(()=>Act("sale.quick",J.O("method","wallet","customerId",mario.S("id"),"items",new[]{J.O("productId",water.S("id"),"quantity",1)})),"wallet payment cannot exceed the balance");
        await Act("wallet.topup",J.O("customerId",mario.S("id"),"amount",1000,"method","card"));
        var walletSale=await Act("sale.quick",J.O("method","wallet","customerId",mario.S("id"),"items",new[]{J.O("productId",water.S("id"),"quantity",1)}));state=await service.Load();
        Check(state.Get("payment",walletSale.S("paymentId")).S("method")=="wallet"&&state.List("walletmove").Where(x=>x.S("customerId")==mario.S("id")).Sum(x=>x.L("amount"))==300,"counter sale paid with wallet debits the balance");
        Check(state.List("movement").Any(x=>x.S("type")=="income"&&x.L("amount")==1000&&x.S("reason").Contains("monedero")),"cash top-up enters the drawer as income");
        await Serve(walletSale.S("visitId"));await Act("visit.deliverAll",J.O("id",walletSale.S("visitId")));
        Check((await service.Load()).List("walletmove").Count(x=>x.S("type")=="earn")==1,"wallet redemptions do not earn cashback");
        await Deny(()=>Act("wallet.pay",J.O("visitId",v2.S("id"),"amount",100)),"wallet payment requires a linked customer");
        await Deny(()=>Act("wallet.adjust",J.O("customerId",mario.S("id"),"amount",-500,"reason","Error")),"wallet cannot go negative");
        Check((await service.State(anaToken)).A("walletmoves").Count>0,"waiters can see wallet balances to charge them");

        // Employee meals: discount, payroll cap and own-meal rule.
        await Act("staff.configure",J.O("discountBps",5000,"monthlyLimit",4000));
        var meal=await Act("staff.meal",J.O("employeeId",ana.S("id"),"settle","payroll","items",new[]{J.O("productId",taco.S("id"),"quantity",2)}));state=await service.Load();
        Check(meal.L("total")==3000&&state.Get("payment",meal.S("paymentId")).S("method")=="payroll"&&state.List("staffcharge").Single().L("amount")==3000,"employee meal applies the staff discount and charges payroll");
        Check(state.List("command").First(x=>x.S("visitId")==meal.S("visitId")).S("label")=="EMPLEADO","kitchen sees employee meals labelled EMPLEADO");
        await Deny(()=>Act("staff.meal",J.O("employeeId",ana.S("id"),"settle","payroll","items",new[]{J.O("productId",water.S("id"),"quantity",2)})),"monthly payroll cap is enforced");
        var own=await Waiter("staff.meal",J.O("settle","card","items",new[]{J.O("productId",water.S("id"),"quantity",1)}));Check(own.L("total")==1000&&own.S("settle")=="card","a waiter registers their own meal paid by card");
        await Deny(()=>Waiter("staff.meal",J.O("employeeId",luis.S("id"),"settle","payroll","items",new[]{J.O("productId",water.S("id"),"quantity",1)})),"a waiter cannot charge another employee's meal");
        var settled=await Act("staff.settle",J.O("employeeId",ana.S("id"),"month",(await service.Load()).List("staffcharge").Single().S("month")));Check(settled.L("count")==1&&(await service.Load()).List("staffcharge").Single().S("status")=="deducted","payroll charges are marked deducted per month");

        // X reading, then Z close once everything is settled.
        var x=await Act("report.x",new());Check(x.S("kind")=="X"&&x.Obj("methods").ContainsKey("payroll")&&x.Obj("methods").ContainsKey("wallet")&&x.L("nextZ")==1,"X reading summarizes payments by method without closing");
        Check(!(await service.Load()).List("zreport").Any(),"X reading does not create a closing record");
        await Deny(()=>Act("report.z",new()),"Z close requires the cash drawer to be closed");
        foreach(var v in (await service.Load()).List("visit").Where(v=>v.S("status")=="open").ToArray()){await Serve(v.S("id"));await Act("visit.deliverAll",J.O("id",v.S("id")));var open=(await service.Load()).Get("visit",v.S("id"));if(open.S("status")=="open"){var balance=(await service.State(token)).A("visits").Objects().First(y=>y.S("id")==v.S("id")).L("balance");if(balance>0)await Act("payment.add",J.O("visitId",v.S("id"),"amount",balance,"method","card"));await Act("visit.close",J.O("id",v.S("id")));}}
        var cash=(await service.Load()).List("cash").Single(c=>c.S("status")=="open");await Act("cash.close",J.O("id",cash.S("id"),"counted",0));
        var z=await Act("report.z",new());state=await service.Load();
        Check(z.L("number")==1&&z.Obj("summary").L("tickets")>=6&&state.Meta.L("zNumber")==1&&state.List("printjob").Any(j=>j.S("type")=="zreport"&&j.S("zreportId")==z.S("id")),"Z close is numbered, stores its totals and queues printing");
        Check(z.S("digest")==J.Hash(J.Canonical(z.Obj("summary"))),"Z summary keeps a digest for tamper detection");

        // ABC by revenue with recipe margin.
        var abc=await Act("report.abc",J.O("from",DateTimeOffset.UtcNow.AddDays(-1).ToString("O"),"to",DateTimeOffset.UtcNow.AddMinutes(1).ToString("O")));var rows=abc.A("rows").Objects().ToArray();var steakRow=rows.First(r=>r.S("productId")==steak.S("id"));
        Check(rows[0].S("class")=="A"&&rows.Sum(r=>r.L("revenue"))==abc.L("revenue")&&rows.Any(r=>r.S("class")!="A"),"ABC ranks products by revenue into A, B and C");
        Check(steakRow.L("unitCost")==3000&&steakRow.L("margin")==steakRow.L("revenue")-6000,"ABC computes margin from the recipe cost");

        // Table QR: token per table, rotation and request import.
        await Deny(()=>Act("table.qr",J.O("id",t1.S("id"))),"table QR requires the public address first");
        await Act("qr.configure",J.O("url","https://pedidos.example.mx/s/loyalty"));var qr=await Act("table.qr",J.O("id",t1.S("id")));var link=qr.S("link");
        Check(System.Text.RegularExpressions.Regex.IsMatch(link,"^https://pedidos\\.example\\.mx/s/loyalty#mesa=[0-9a-f]{32}$"),"table QR link carries a random token in the fragment");
        Check((await Act("table.qr",J.O("id",t1.S("id")))).S("link")==link&&(await Act("table.qr",J.O("id",t1.S("id"),"rotate",true))).S("link")!=link,"QR is stable until rotated");
        await Deny(()=>Waiter("table.qr",J.O("id",t1.S("id"))),"only a manager prints table QR codes");
        state=await service.Load();var table=state.Get("table",t1.S("id"));await Act("table.clean",J.O("id",t1.S("id")));var v3=await Act("visit.open",J.O("tableId",t1.S("id"),"people",2,"waiterId",ana.S("id")));state=await service.Load();
        JsonObject Proposal(string kind,string hash)=>J.O("id",J.Id(),"digest","d","status","pending-review","command",J.O("type","table","payload",J.O("kind",kind,"tableId",table.S("id"),"qrHash",hash,"items",new JsonArray())));
        var call=Proposal("bill",table.S("qrHash"));service.Domain.ImportTableRequest(state,call);
        Check(call.S("status")=="accepted-local"&&state.List("notification").Any(n=>n.S("visitId")==v3.S("id")&&n.S("waiterId")==ana.S("id")&&n.S("message").Contains("pide la cuenta")),"QR bill request notifies the table's waiter");
        var stale=Proposal("waiter",J.Hash("old"));service.Domain.ImportTableRequest(state,stale);Check(stale.S("status")=="rejected-local","rotated QR codes are rejected");
        var order=Proposal("order",table.S("qrHash"));service.Domain.ImportTableRequest(state,order);Check(order.S("status")=="pending-review"&&order.S("visitId")==v3.S("id")&&state.List("notification").Any(n=>n.S("type")=="qr-order"),"QR order waits for the waiter's confirmation");
        }finally{runtime.Dispose();}
    }
}
