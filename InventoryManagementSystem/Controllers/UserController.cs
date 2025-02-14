﻿using InventoryManagementSystem.Models.EF;
using InventoryManagementSystem.Models.NotificationModels;
using InventoryManagementSystem.Models.Password;
using InventoryManagementSystem.Models.reCAPTCHA;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace InventoryManagementSystem.Controllers
{
    public class UserController : Controller
    {
        private readonly InventoryManagementSystemContext _dbContext;
        private readonly IConfiguration Config;

        public UserController(InventoryManagementSystemContext dbContext, IConfiguration config)
        {
            _dbContext = dbContext;
            Config = config;
        }

        [HttpGet("login")]
        public IActionResult Login()
        {
            return View();
        }

        [HttpPost("login")]
        public async Task<IActionResult> Authenticate(string username, string password)
        {
            // 登入成功會 redirect，如果失敗就會調用 Login View，要顯示錯誤訊息
            ViewData["LoginFailed"] = true;

            if(string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                return View("Login");
            }
            User user = await _dbContext.Users
                .Where(u => u.Username == username)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                return View("Login");
            }

            PBKDF2 hasher = new PBKDF2(password, user.Salt);

            if(hasher.HashedPassword.SequenceEqual(user.HashedPassword))
            {

                List<Claim> claims = new List<Claim>();
                claims.Add(new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()));
                claims.Add(new Claim(ClaimTypes.SerialNumber, user.UserSn.ToString()));
                claims.Add(new Claim(ClaimTypes.Name, user.Username));
                claims.Add(new Claim(ClaimTypes.Email, user.Email));
                claims.Add(new Claim(ClaimTypes.Role, "user"));
                ClaimsIdentity identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                ClaimsPrincipal principal = new ClaimsPrincipal(identity);
                await HttpContext.SignInAsync(principal);
                return RedirectToAction("EquipQry", "Equips");
            }
            else
            {
                return View("Login");
            }

        }

        [Authorize(Roles = "user")]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync();
            return RedirectToAction("Index", "Home");
        }

        [HttpGet("signup")]
        public IActionResult SignUp()
        {
            return View();
        }

        [HttpGet("login/line")]
        public IActionResult BindLine()
        {
            return View();
        }

        [HttpGet("/account")]
        [Authorize]
        public IActionResult UserManagement()
        {
            bool isAdmin = User.IsInRole("admin");

            if(isAdmin)
                return View("adminManageUser");
            else
                return View("userManage");

        }

        [HttpGet("password/forget")]
        public IActionResult ForgetPassword()
        {
            ViewData["reCAPTCHASiteKey"] = Config.GetSection("reCAPTCHA").Get<reCAPTCHAConfig>().SiteKey;
            return View();
        }
        
        [HttpGet("/resetpassword")]
        public async Task<IActionResult> ResetPassword(string token)
        {
            return View();
        }
    }
}
