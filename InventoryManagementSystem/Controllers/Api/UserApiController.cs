﻿using InventoryManagementSystem.Models.EF;
using InventoryManagementSystem.Models.Interfaces;
using InventoryManagementSystem.Models.ViewModels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace InventoryManagementSystem.Controllers.Api
{
    [Route("api/user")]
    [ApiController]
    public class UserApiController : ControllerBase, IHashPassword
    {
        private readonly InventoryManagementSystemContext _dbContext;

        public UserApiController(InventoryManagementSystemContext dbContext)
        {
            _dbContext = dbContext;
        }

        /* GET
         * api/user/validate?validatedField={FieldName}&value={Value}
         * 
         * FieldName accepts 3 values: 'username', 'email', 'phoneNumber'.
         * 
         * output: True meaning the field value is available;
         *         False meaning the field value is unavailable.
         */
        // 驗證 username、email、phoneNumber 是否可被註冊
        [HttpGet("validate")]
        public async Task<IActionResult> ValidateUser(string validatedField, string value)
        {
            if(string.IsNullOrEmpty(value))
            {
                return Ok(false);
            }

            bool userExists;
            switch(validatedField)
            {
                case "username":
                    userExists = await _dbContext.Users.AnyAsync(u => u.Username == value);
                    break;
                case "email":
                    userExists = await _dbContext.Users.AnyAsync(u => u.Email == value);
                    break;
                case "phoneNumber":
                    userExists = await _dbContext.Users.AnyAsync(u => u.PhoneNumber == value);
                    break;
                default:
                    return Ok(false);
            }

            if(userExists)
                return Ok(false);

            return Ok(true);
        }

        /* POST
         * api/user/
         * 
         * input: A JSON object having the same structure as PostUserViewModel
         *        in which Username, Email, Password, Fullname and PhoneNumber
         *        are required.
         * 
         * output: 1. Redirect (Found 302) to Equip page if success.
         *         2. Bad Request 400 if any required field is empty.
         *         3. Conflict 409 if failing to update the database.
         */
        // 註冊用 API
        [HttpPost]
        [Consumes("application/json")]
        public async Task<IActionResult> PostUser(PostUserViewModel model)
        {
            string[] notNullFields =
            {
                model.Username,
                model.Email,
                model.Password,
                model.FullName,
                model.PhoneNumber
            };

            bool nullOrWhiteSpaceExist = notNullFields
                .Any(f => string.IsNullOrWhiteSpace(f));

            if(nullOrWhiteSpaceExist)
            {
                return BadRequest();
            }

            IHashPassword hasher = this as IHashPassword;
            Random r = new Random();
            byte[] saltBytes = new byte[32];
            byte[] passwordBytes = Encoding.UTF8.GetBytes(model.Password);
            r.NextBytes(saltBytes);
            byte[] hashedPassword = hasher.HashPasswordWithSalt(passwordBytes, saltBytes);

            User user = new User
            {
                Username = model.Username,
                Email = model.Email,
                HashedPassword = hashedPassword,
                Salt = saltBytes,
                FullName = model.FullName,
                AllowNotification = model.AllowNotification,
                Address = model.Address,
                PhoneNumber = model.PhoneNumber,
                Gender = model.Gender,
                DateOfBirth = model.DateOfBirth,
                CreateTime = DateTime.Now,
                LineAccount = model.LineAccount,
            };

            _dbContext.Users.Add(user);

            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                return Conflict();
            }

            // 註冊成功後直接發 cookie，視同登入。
            List<Claim> claims = new List<Claim>();
            claims.Add(new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString(), ClaimValueTypes.Integer32));
            claims.Add(new Claim(ClaimTypes.Name, user.Username));
            claims.Add(new Claim(ClaimTypes.Role, "user"));
            ClaimsIdentity identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            ClaimsPrincipal principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(principal);
            return RedirectToAction("equipQryUser", "Equips");

        }

        /* PUT
         * api/user/{UserID}
         * 
         * input: A JSON object having the same structure as PutUserViewModel
         *        in which Username, Email, OldPassword, Fullname and PhoneNumber
         *        are required.
         *        Note that Password here means the new one, while OldPassword
         *        means, obviously, the old one.
         * 
         * output: 1. Ok 200 if success.
         *         2. Unauthorized 401 if you are not an admin or the owner of the account.
         *         3. Bad Request 400 if any required field is empty or OldPassword is wrong.
         *         4. Conflict 409 if failing to update the database.
         */
        // 修改 User 資訊的 API。Admin 可改所有 User 的資訊；而 User 只能改自己的。
        [HttpPut("{id}")]
        [Consumes("application/json")]
        [Authorize]
        public async Task<IActionResult> PutUser(int id, PutUserViewModel model)
        {
            #region 檢查目前正在修改的人是否為管理員或本人
            // Admin 可以改所有 User 的資料
            bool isAdmin = User.HasClaim(ClaimTypes.Role, "admin");

            // User 只能改自己的資料
            if(!isAdmin)
            {
                string userIdString = User.Claims
                    .FirstOrDefault(c => c.Type == ClaimTypes.Role)
                    .Value;

                int userId = int.Parse(userIdString);

                if(userId != id)
                    return Unauthorized();
            }
            #endregion

            #region 檢查是否所有 required field 是都有填寫
            List<string> notNullFields = new List<string>
            {
                model.Username,
                model.Email,
                model.FullName,
                model.PhoneNumber
            };

            // 管理員修改時不需填寫密碼
            // 只有使用者修改時需要填
            if(!isAdmin)
                notNullFields.Add(model.OldPassword);

            bool nullOrWhiteSpaceExist = notNullFields
                .Any(f => string.IsNullOrWhiteSpace(f));

            if(nullOrWhiteSpaceExist)
            {
                return BadRequest();
            }
            #endregion

            #region The mapping process between the view model and the EF model
            User user = await _dbContext.Users.FindAsync(id);

            // 不是管理員就必須填入正確的密碼
            IHashPassword hasher = this as IHashPassword;
            if(!isAdmin)
            {
                byte[] oldPwBytes = Encoding.UTF8.GetBytes(model.OldPassword);
                byte[] hashedOldPw = hasher.HashPasswordWithSalt(oldPwBytes, user.Salt);

                if(!hashedOldPw.SequenceEqual(user.HashedPassword))
                    return BadRequest();
            }

            // 有輸入新密碼才修改密碼
            if(model.Password != null)
            {
                byte[] newPwBytes = Encoding.UTF8.GetBytes(model.Password);
                Random r = new Random();
                byte[] saltBytes = new byte[32];
                r.NextBytes(saltBytes);
                byte[] hashedNewPw = hasher.HashPasswordWithSalt(newPwBytes, saltBytes);

                user.Salt = saltBytes;
                user.HashedPassword = hashedNewPw;
            }

            // 修改其他個資
            user.Email = model.Email;
            user.PhoneNumber = model.PhoneNumber;
            user.Username = model.Username;
            user.FullName = model.FullName;
            user.AllowNotification = model.AllowNotification;
            user.Address = model.Address;
            user.Gender = model.Gender;
            user.DateOfBirth = model.DateOfBirth;
            user.LineAccount = model.LineAccount;
            #endregion

            #region Update the database
            try
            {
                await _dbContext.SaveChangesAsync();
            }
            catch
            {
                return Conflict();
            }
            #endregion

            return Ok();
        }
    }
}
