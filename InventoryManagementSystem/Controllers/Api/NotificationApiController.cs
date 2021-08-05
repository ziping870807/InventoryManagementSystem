﻿using InventoryManagementSystem.Models.EF;
using InventoryManagementSystem.Models.LINE;
using InventoryManagementSystem.Models.NotificationModels;
using InventoryManagementSystem.Models.ResultModels;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;

namespace InventoryManagementSystem.Controllers.Api
{
    [Route("api/notification")]
    [ApiController]
    public class NotificationApiController : ControllerBase
    {
        private readonly InventoryManagementSystemContext _dbContext;
        private readonly NotificationService _notificationService;
        private readonly NotificationConfig _notificaionConfig;
        private readonly LineConfig _lineConfig;
        private static HttpClient Client = new HttpClient();

        public NotificationApiController(
            InventoryManagementSystemContext dbContext,
            IOptions<NotificationConfig> notificationConfig,
            IOptions<LineConfig> lineConfig,
            NotificationService notificationService)
        {
            _dbContext = dbContext;
            _notificationService = notificationService;
            _notificaionConfig = notificationConfig.Value;
            _lineConfig = lineConfig.Value;
        }

        [HttpPost]
        public async Task<IActionResult> PostNotification()
        {
            string authString = HttpContext.Request.Headers["Authorization"];

            if(authString == null || !authString.StartsWith("basic"))
                return Unauthorized();

            string authKey = authString.Substring("basic ".Length).Trim();

            if(authKey != _notificaionConfig.ApiKey)
                return Unauthorized();

            var usersWithOverdueOrder = await _dbContext.Users
                .Where(u => u.Orders.Any(o => o.OrderStatusId == "A"))
                .Where(u => u.Orders.Any(o => o.OrderDetails.Any(od => od.OrderDetailStatusId == "T")))
                .Where(u => u.Orders.Any(o => o.EstimatedPickupTime.AddDays(o.Day) < DateTime.Now))
                .Where(u => u.AllowNotification == true)
                .Select(u => new
                {
                    UserId = u.UserId,
                    FullName = u.FullName,
                    Username = u.Username,
                    Email = u.Email,
                    LineId = u.LineId,
                    OverdueOrders = u.Orders
                        .Where(o => o.OrderStatusId == "A")
                        .Where(o => o.OrderDetails.Any(od => od.OrderDetailStatusId == "T"))
                        .Where(o => o.EstimatedPickupTime.AddDays(o.Day) < DateTime.Now)
                        .Select(o => new
                        {
                            EquipmentName = o.Equipment.EquipmentName,
                            Number = o.OrderDetails.Count(od => od.OrderDetailStatusId == "T")
                        })
                })
                .ToArrayAsync();

            foreach(var user in usersWithOverdueOrder)
            {
                #region Build text message
                StringBuilder builder = new StringBuilder();
                builder.AppendFormat("@{0} 您好：\n\n", user.Username);
                foreach(var order in user.OverdueOrders)
                {
                    builder.AppendFormat("您所租借的「{0}」仍有 {1} 筆尚未歸還。\n", order.EquipmentName, order.Number);
                }
                builder.Append("\n");
                builder.Append("請您儘速歸還，感謝您的配合。");

                string lineText = builder.ToString();

                builder.Append("<br>");
                builder.Replace("\n", "<br>");
                builder.Append("本信為系統自動發送，請勿直接回覆此信件。");
                builder.Insert(0, "<p>");
                builder.Append("</p>");
                string emailText = builder.ToString();
                #endregion

                #region Send Email
                await _notificationService.SendEmailNotification(
                    user.FullName,
                    user.Email,
                    "逾期通知",
                    "html",
                    emailText);
                #endregion

                #region Send Line Message
                // 有綁定 LINE 才傳送訊息
                if(string.IsNullOrWhiteSpace(user.LineId))
                    continue;

                await _notificationService.SendLineNotification(user.LineId, lineText, user.UserId);
                #endregion
            }

            return Ok();
        }

        [HttpPost("contactus")]
        public async Task<IActionResult> ContactUs(
            [FromForm] string name,
            [FromForm] string email,
            [FromForm] string feedback)
        {
            StringBuilder builder = new StringBuilder(feedback);
            builder.Replace("\n", "<br>");
            builder.Append($"<br>聯絡信箱：{email}");
            builder.Insert(0, "<p>");
            builder.Append("</p>");
            await _notificationService.SendEmailNotification(_notificaionConfig.Name, _notificaionConfig.User, $"來自「{name}」的意見反映", "html", builder.ToString());
            return Ok();
        }
    }
}
