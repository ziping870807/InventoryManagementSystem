﻿using InventoryManagementSystem.Models.EF;
using InventoryManagementSystem.Models.NotificationModels;
using InventoryManagementSystem.Models.ResultModels;
using InventoryManagementSystem.Models.ViewModels;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace InventoryManagementSystem.Controllers.Api
{

    [Route("[controller]/[action]")]
    [ApiController]
    public class OrderApiController : ControllerBase
    {

        private readonly InventoryManagementSystemContext _dbContext;
        private readonly NotificationService notificationService;
        private readonly NotificationConfig _notificationConfig;

        public OrderApiController(
            InventoryManagementSystemContext dbContext, 
            IOptions<NotificationConfig> config,
            NotificationService notificationService)
        {
            _dbContext = dbContext;
            this.notificationService = notificationService;
            _notificationConfig = config.Value;
        }


        /*
         * OrderApi/MakeOrder
         */
        // 下訂單
        [HttpPost]
        [Consumes("application/json")]
        [Authorize(Roles = "user")]
        public async Task<IActionResult> MakeOrder(MakeOrderViewModel model)
        {
            model.EstimatedPickupTime = model.EstimatedPickupTime.AddHours(8);
            if(model.EstimatedPickupTime < DateTime.Today)
            {
                return BadRequest();
            }

            // Get UserID
            string userIDString = User.Claims
                .FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)
                .Value;
            Guid orderId = Guid.NewGuid();
            Order order = new Order
            {
                UserId = Guid.Parse(userIDString),
                EquipmentId = model.EquipmentId,
                Quantity = model.Quantity,
                EstimatedPickupTime = model.EstimatedPickupTime,
                Day = model.Day,

                // 前端沒權限給的
                OrderStatusId = "P",
                OrderTime = DateTime.Now,
                OrderId = orderId
            };

            _dbContext.Orders.Add(order);

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                return Conflict();
            }

            return Ok();

        }

        [HttpGet]
        [Produces("application/json")]
        // 所有訂單、待核可、待付款、待領取、租借中、已結束、已逾期
        [Authorize]
        public async Task<IActionResult> GetOrders(bool noPending, bool noOutstanding,
                                                   bool noReady, bool noOnGoing,
                                                   bool noClosed, bool noOverdue)
        {
            IQueryable<Order> tempOrders = null;

            //  Could also use User.IsInRole("user")
            if(User.HasClaim(ClaimTypes.Role, "user"))
            {
                // User 只看得到自己的 Order
                string userIdString = User.Claims
                    .FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)
                    .Value;
                Guid userId = Guid.Parse(userIdString);

                tempOrders = _dbContext.Orders
                    .Where(o => o.UserId == userId);
            }
            else if(User.HasClaim(ClaimTypes.Role, "admin"))
            {
                // 管理員可以看到所有人的 Order
                tempOrders = _dbContext.Orders.Select(o => o);
            }

            OrderResultModel[] orders = await tempOrders.Select(o => new OrderResultModel
            {
                OrderId = o.OrderId,
                OrderSn = o.OrderSn,
                UserId = o.UserId,
                EquipmentId = o.EquipmentId,
                Quantity = o.Quantity,
                EstimatedPickupTime = o.EstimatedPickupTime,
                Day = o.Day,
                ExpireTime = o.EstimatedPickupTime.AddDays(o.Day),
                OrderStatusId = o.OrderStatusId,
                OrderTime = o.OrderTime,

                EquipmentSn = o.Equipment.EquipmentSn,
                EquipmentName = o.Equipment.EquipmentName,
                Brand = o.Equipment.Brand,
                Model = o.Equipment.Model,
                UnitPrice = o.Equipment.UnitPrice,
                Description = o.Equipment.Description,

                Username = o.User.Username,

                StatusName = o.OrderStatus.StatusName,

                OpenReportCount = o.OrderDetails
                    .SelectMany(od => od.Reports)
                    .Count(r => r.CloseTime == null),

                OrderDetails = o.OrderDetails.Select(od => new OrderDetailResultModel
                {
                    OrderDetailId = od.OrderDetailId,
                    OrderDetailSn = od.OrderDetailSn,
                    ItemId = od.ItemId,
                    ItemSn = od.Item.ItemSn,
                    OpenReportCounts = od.Reports.Count(r => r.CloseTime == null),
                    ItemDescription = od.Item.Description,

                    OrderDetailStatusId = od.OrderDetailStatusId,
                    OrderDetailStatus = od.OrderDetailStatus.StatusName,

                    ItemStatus = od.ItemLogs
                        .OrderByDescending(il => il.CreateTime)
                        .Select(il => il.Condition.ConditionName)
                        .FirstOrDefault()

                }).ToArray(),

                PaymentId = o.PaymentOrder.PaymentId,

                TotalRentalFee = o.Equipment.UnitPrice * o.Quantity * o.Day,

                TotalExtraFee = o.OrderDetails
                                .SelectMany(od => od.ExtraFees)
                                .Select(f => f.Fee)
                                .ToArray()
                                .Sum(),

                SamePaymentOrder=o.PaymentOrder.Payment.PaymentOrders
                                 .Select(po=>po.OrderId)
                                 .ToArray(),

                PaymentReceived=o.PaymentOrder.Payment.PaymentDetails
                                .Select(pd=>pd.AmountPaid)
                                .ToArray()
                                .Sum(),

                
                                


            }).ToArrayAsync();

            foreach (OrderResultModel od in orders)
            {
               
                decimal[] extraFee = new decimal[od.SamePaymentOrder.Length];
                decimal[] rentalFee = new decimal[od.SamePaymentOrder.Length];
                
                var extraList = extraFee.ToList();
                var rentalList = rentalFee.ToList();
                
                for (var i = 0; i < od.SamePaymentOrder.Length; i++)
                {
                   
                    decimal odExtraFee = Array.Find(orders, o => o.OrderId == od.SamePaymentOrder[i]).TotalExtraFee;
                    decimal odRentalFee = Array.Find(orders, o => o.OrderId == od.SamePaymentOrder[i]).TotalRentalFee;
                   

                    extraList.Add(odExtraFee);
                    rentalList.Add(odRentalFee);
                    
                }

                od.TotalPaymentFee = extraList.Sum() + rentalList.Sum();
            }
            
            
            IEnumerable<OrderResultModel> orderResults = new OrderResultModel[0];

            //"待核可"
            if (!noPending)
            {
                OrderResultModel[] pendingOrders = orders.Where(o =>
                    o.OrderStatusId == "P" && // Pending
                    o.EstimatedPickupTime >= DateTime.Today) // 尚未過期的 order
                    .ToArray();
                Array.ForEach(pendingOrders, o => o.TabName = "待核可");
                orderResults = orderResults.Concat(pendingOrders);
            }

            //"待付款"
            if(!noOutstanding)
            {
                OrderResultModel[] outstandingOrders = orders.Where(o =>
                o.OrderStatusId == "A" && // Order 是核可的
                o.PaymentId == null && // 沒付款
                o.EstimatedPickupTime >= DateTime.Today) // 現在還沒過取貨時間
                    .ToArray();
                Array.ForEach(outstandingOrders, o => o.TabName = "待付款");
                orderResults = orderResults.Concat(outstandingOrders);
            }

            //"待領取"
            if(!noReady)
            {
                OrderResultModel[] readyOrders = orders.Where(o =>
                    o.OrderStatusId == "A" && // Order 是核可的
                    o.OrderDetails.Any(od => od.OrderDetailStatusId == "P") && // order 底下的 order detail 有待取貨的
                    o.EstimatedPickupTime.AddDays(o.Day) >= DateTime.Today && // 沒過期的
                    o.PaymentId != null) // 已付款
                    .ToArray();
                Array.ForEach(readyOrders, o => o.TabName = "待領取");
                orderResults = orderResults.Concat(readyOrders);
            }

            //"租借中"
            if(!noOnGoing)
            {
                OrderResultModel[] ongoinOrders = orders.Where(o =>
                    o.OrderStatusId == "A" && // 被核准的 order
                    o.OrderDetails.Any(od => od.OrderDetailStatusId == "T") &&
                    o.OrderDetails.All(od => od.OrderDetailStatusId != "P") && // 所訂的物品已被取貨
                    o.EstimatedPickupTime.AddDays(o.Day) >= DateTime.Today) // 尚未逾期
                    .ToArray();
                Array.ForEach(ongoinOrders, o => o.TabName = "租借中");
                orderResults = orderResults.Concat(ongoinOrders);
            }

            //"已結束"
            if(!noClosed)
            {
                string[] restrictions = { "C", "E", "D" }; // Canceled, ended and denied.
                OrderResultModel[] closedOrders = orders.Where(o =>
                    restrictions.Contains(o.OrderStatusId) || // 已取消、已結束、已拒絕
                    o.OrderStatusId == "P" && o.EstimatedPickupTime < DateTime.Today) // 逾期回應
                    .ToArray();
                Array.ForEach(closedOrders, o => o.TabName = "已結束");
                orderResults = orderResults.Concat(closedOrders);
            }

            //"已逾期"
            if(!noOverdue)
            {
                OrderResultModel[] overdueOrders = orders.Where(o =>
                    o.OrderStatusId == "A" &&
                    o.OrderDetails.Any(od => od.OrderDetailStatusId == "T") &&
                    o.EstimatedPickupTime.AddDays(o.Day) < DateTime.Today)
                    .ToArray();
                Array.ForEach(overdueOrders, o => o.TabName = "已逾期");
                orderResults = orderResults.Concat(overdueOrders);
            }


            return Ok(orderResults);

        }

        /*
         * OrderApi/RespondOrder
         */
        // Admin 核准或拒絕訂單
        [HttpPost]
        [Consumes("application/json")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> RespondOrder(RespondOrderViewModel model)
        {
            var order = await _dbContext.Orders
                .FirstOrDefaultAsync(o => o.OrderId == model.OrderID &&
                    o.OrderStatusId == "P" && // 待審核的 order
                    o.EstimatedPickupTime >= DateTime.Today && // 尚未過期
                    o.PaymentOrder == null); // 尚未付款

            // 找不到訂單
            if(order == null)
            {
                return NotFound();
            }

            // 防止同個 item 被分配多次
            Guid[] itemIDs = model.ItemIDs.Distinct().ToArray();

            Item[] items = await _dbContext.Items
                .Where(i => itemIDs.Contains(i.ItemId))
                .ToArrayAsync();

            // Get AdminID
            string adminIdString = User.Claims
                .FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)
                .Value;
            Guid adminId = Guid.Parse(adminIdString);

            Response response = new Response
            {
                ResponseId = Guid.NewGuid(),
                OrderId = model.OrderID,
                AdminId = adminId
            };

            if(model.Reply == true)
            {
                // 訂單寫的數量與實際分配的數量不一致
                if(order.Quantity != itemIDs.Length)
                {
                    return BadRequest();
                }

                // 存在有分配的設備非訂單所寫的設備
                bool invalidEquipIdExists = items
                    .Any(i => i.EquipmentId != order.EquipmentId);
                
                if(invalidEquipIdExists)
                {
                    return BadRequest();
                }

                // 庫存不夠，無法滿足訂單
                int inStockNumber = await _dbContext.Items
                    .AsNoTracking()
                    .Where(i => i.EquipmentId == order.EquipmentId)
                    .CountAsync(i => i.ConditionId == "I");
                if(inStockNumber < order.Quantity)
                {
                    return BadRequest();
                }



                // 這裡 condition 也可以用 itemIDs.length
                // 因為執行到這邊已經保證 items 跟 itemIDs 長度一樣
                for(int i = 0; i < items.Length; i++)
                {
                    items[i].ConditionId = "P"; // Pending
                }

                // 每個 item 都要新增一筆 OrderDetail 的記錄
                OrderDetail[] details = new OrderDetail[items.Length];
                ItemLog[] logs = new ItemLog[items.Length];

                for(int i = 0; i < items.Length; i++)
                {
                    details[i] = new OrderDetail
                    {
                        OrderDetailId = Guid.NewGuid(),
                        OrderId = model.OrderID,
                        ItemId = items[i].ItemId,
                        OrderDetailStatusId = "P" // Pending
                    };

                    logs[i] = new ItemLog
                    {
                        ItemLogId = Guid.NewGuid(),
                        OrderDetailId = details[i].OrderDetailId,
                        AdminId = adminId,
                        ItemId = details[i].ItemId,
                        ConditionId = "P"  // Pending
                    };
                }
                _dbContext.OrderDetails.AddRange(details);
                _dbContext.ItemLogs.AddRange(logs);

                order.OrderStatusId = "A"; // Approved
                response.Reply = "Y"; // Yes
            }
            else 
            {
                order.OrderStatusId = "D"; // Denied
                response.Reply = "N"; // No
            }

            _dbContext.Responses.Add(response);

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                return Conflict();
            }


            // 通知 User
            var user = await _dbContext.Users
                .Where(u => u.UserId == order.UserId)
                .Select(u => new
                {
                    u.UserId,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.LineId,
                    u.AllowNotification
                })
                .FirstOrDefaultAsync();

            if(!user.AllowNotification?? false)
            {
                return Ok();
            }

            string equipName = await _dbContext.Equipment
                .Where(eq => eq.EquipmentId == order.EquipmentId)
                .Select(eq => eq.EquipmentName)
                .FirstOrDefaultAsync();


            StringBuilder builder = new StringBuilder();
            string subject = string.Empty;
            if(model.Reply == true)
            {
                subject = "申請租借核可通知";
                builder.AppendFormat("@{0} 您好：\n\n", user.Username);
                builder.AppendFormat("您所申請租借的「{0}」已被核可，請儘速前往結帳，謝謝。", equipName);
            }
            else
            {
                subject = "申請租借拒絕通知";
                builder.AppendFormat("@{0} 您好：\n\n", user.Username);
                builder.AppendFormat("很抱歉，您所申請租借的「{0}」遭拒絕。", equipName);
            }


            if(!string.IsNullOrWhiteSpace(user.LineId))
            {
                string lineText = builder.ToString();
                await notificationService.SendLineNotification(user.LineId, lineText, user.UserId);
            }

            builder.Append("\n");
            builder.Append("本信為系統自動發送，請勿直接回覆此郵件。");
            builder.Replace("\n", "<br>");
            builder.Insert(0, "<p>");
            builder.Append("</p>");

            string emailText = builder.ToString();
            await notificationService.SendEmailNotification(
                user.FullName,
                user.Email,
                subject, 
                "html",
                emailText);


            return Ok();
        }

        /*
         * OrderApi/CancelOrder
         */
        // 取消 Order
        [HttpPost]
        [Consumes("application/json")]
        [Authorize]
        public async Task<IActionResult> CancelOrder(CancelOrderViewModel model)
        {
            Order order = await _dbContext.Orders
                .Where(o => o.OrderId == model.OrderID)
                .Where(o => o.PaymentOrder == null) //尚未付款才能取消
                .FirstOrDefaultAsync();


            if(order == null)
            {
                return NotFound();
            }


            OrderDetail[] details = await _dbContext.OrderDetails
                .Where(od => od.OrderId == order.OrderId)
                .ToArrayAsync();

            // 訂單改為取消狀態
            order.OrderStatusId = "C";

            // CanceledOrder 新增一筆紀錄
            CanceledOrder co = new CanceledOrder
            {
                OrderId = order.OrderId,
                UserId = order.UserId,
                Description = model.Description,
                CancelTime = DateTime.Now
            };
            _dbContext.CanceledOrders.Add(co);


            // 若 admin 已分配 item 給這筆 order
            // 還要再額外
            // 1. 把 item 的 condition 改回再庫（ItemLog 也要記錄 item 的改變）
            // 2. 把 order detail 的 status 改成取消
            if(details.Length != 0)
            {
                Guid[] itemIDs = details
                    .Select(od => od.ItemId)
                    .ToArray();

                Item[] items = await _dbContext.Items
                    .Where(i => itemIDs.Contains(i.ItemId))
                    .ToArrayAsync();

                foreach(OrderDetail detail in details)
                {
                    // item 的狀態改成入庫
                    Item item = items
                        .Where(i => i.ItemId == detail.ItemId)
                        .FirstOrDefault();
                    item.ConditionId = "I";

                    // order detail 的狀態改成取消
                    detail.OrderDetailStatusId = "C";


                    // ItemLog 新增一筆資料
                    ItemLog log = new ItemLog
                    {
                        ItemLogId = Guid.NewGuid(),
                        OrderDetailId = detail.OrderDetailId,
                        ItemId = detail.ItemId,
                        ConditionId = "I",
                        Description = model.Description,
                        CreateTime = DateTime.Now
                    };
                    if(User.HasClaim(ClaimTypes.Role, "admin"))
                    {
                        string adminIdString = User.Claims
                            .FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)
                            .Value;
                        Guid adminId = Guid.Parse(adminIdString);

                        log.AdminId = adminId;
                    }

                    _dbContext.ItemLogs.Add(log);
                }
            }

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                return Conflict();
            }

            return Ok();
        }

        /*
         * OrderApi/CompleteOrder/{OrderID}
         */
        // 管理員確認訂單完成（該標記歸還的已標記歸還、該標記遺失的已標記遺失）
        // 可能用不到了。
        [HttpPost]
        [Route("{id}")]
        [Authorize(Roles = "admin")]
         async Task<IActionResult> CompleteOrder(Guid id)
        {
            Order order = await _dbContext.Orders
                .Where(o => o.OrderId == id &&
                    o.OrderDetails.All(od => od.OrderDetailStatusId != "T"))
                .FirstOrDefaultAsync();

            if(order == null)
            {
                return NotFound();
            }

            order.OrderStatusId = "E"; // Ended

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                return Conflict();
            }

            return Ok(id);
        }
    }
}
