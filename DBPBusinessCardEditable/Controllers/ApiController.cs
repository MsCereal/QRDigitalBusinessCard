using DBPBusinessCardEditable.Models;
using DBPBusinessCardEditable.Models;
using DBPBusinessCardEditable.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;

namespace DBPBusinessCardEditable.Controllers
{
    /// <summary>DBP Digital Business Card REST API</summary>
    [ApiController]
    [Route("api/cards")]
    [Produces("application/json")]
    public class CardsApiController : ControllerBase
    {
        private readonly SqliteCardService _service;
        public CardsApiController(SqliteCardService service) => _service = service;

        private bool IsAuthorized()
        {
            string expected = Environment.GetEnvironmentVariable("API_KEY") ?? "dbp-api-2026";
            Request.Headers.TryGetValue("X-Api-Key", out var headerKey);
            Request.Query.TryGetValue("apiKey", out var queryKey);
            return headerKey == expected || queryKey == expected;
        }
        private IActionResult Unauthorized401() =>
            StatusCode(401, new { error = "Unauthorized. Provide API key via X-Api-Key header or ?apiKey= query." });

        private object CardSummary(CardProfile card, bool includeCardUrl = true)
        {
            var url = $"{Request.Scheme}://{Request.Host}/card/{card.CardToken}";
            return new
            {
                empId       = card.EmpId,
                name        = card.Name,
                title       = card.Title,
                org         = card.Org,
                phone       = card.ShowPhone  ? card.Phone     : null,
                email       = card.ShowEmail  ? card.Email     : null,
                office      = card.ShowOffice ? card.Office    : null,
                github      = card.ShowGitHub ? card.GitHub    : null,
                linkedin    = card.ShowLinkedIn ? card.LinkedIn : null,
                portfolio   = card.ShowPortfolio ? card.Portfolio : null,
                hasPhoto    = !string.IsNullOrEmpty(card.Photo),
                cardUrl     = url,
                qrUrl       = $"{Request.Scheme}://{Request.Host}/QR/Generate/{card.EmpId}",
                lastUpdated = card.LastUpdated
            };
        }

        /// <summary>Get a single employee card by Employee ID (public).</summary>
        /// <param name="empId">Employee ID (format: 7digits-3letters, e.g. 0000001-DBP)</param>
        [HttpGet("{empId}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        public IActionResult GetCard(string empId)
        {
            var card = _service.Get(empId);
            if (card == null)
                return NotFound(new { error = $"No card found for Employee ID: {empId}" });
            return Ok(CardSummary(card));
        }

        /// <summary>Get all employee cards. Requires API key.</summary>
        [HttpGet]
        [ProducesResponseType(200)]
        [ProducesResponseType(401)]
        public IActionResult GetAllCards()
        {
            if (!IsAuthorized()) return Unauthorized401();
            var cards = _service.GetAll();
            var result = new List<object>();
            foreach (var card in cards)
                result.Add(CardSummary(card));
            return Ok(new { total = result.Count, cards = result });
        }

        /// <summary>Create or update an employee card. Requires API key.</summary>
        [HttpPost]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(401)]
        public IActionResult UpsertCard([FromBody] CardProfile model)
        {
            if (!IsAuthorized()) return Unauthorized401();
            if (model == null || string.IsNullOrWhiteSpace(model.EmpId))
                return BadRequest(new { error = "empId is required." });
            if (!System.Text.RegularExpressions.Regex.IsMatch(model.EmpId.Trim(), @"^\d{7}-[A-Za-z]{3}$"))
                return BadRequest(new { error = "empId must be 7 digits-3 letters (e.g. 0000001-DBP)" });
            if (string.IsNullOrWhiteSpace(model.Name))
                return BadRequest(new { error = "name is required." });

            // Use EmpId as SamAccountName if not supplied (API creates cards without a Windows session)
            if (string.IsNullOrWhiteSpace(model.SamAccountName))
                model.SamAccountName = model.EmpId.Trim();

            _service.Save(model);
            var saved = _service.Get(model.EmpId.Trim());
            return Ok(new
            {
                success = true,
                message = $"Card for {model.EmpId} saved.",
                cardUrl = $"{Request.Scheme}://{Request.Host}/card/{saved!.CardToken}"
            });
        }

        /// <summary>Delete an employee card by Employee ID. Requires API key.</summary>
        /// <param name="empId">Employee ID to delete</param>
        [HttpDelete("{empId}")]
        [ProducesResponseType(200)]
        [ProducesResponseType(401)]
        [ProducesResponseType(404)]
        public IActionResult DeleteCard(string empId)
        {
            if (!IsAuthorized()) return Unauthorized401();
            var card = _service.Get(empId);
            if (card == null)
                return NotFound(new { error = $"No card found for Employee ID: {empId}" });
            _service.Delete(card.SamAccountName); // delete by SAM (primary key)
            return Ok(new { success = true, message = $"Card for {empId} deleted." });
        }
    }
}
