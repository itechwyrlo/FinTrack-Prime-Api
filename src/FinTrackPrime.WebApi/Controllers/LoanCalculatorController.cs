using FinTrackPrime.Business.Interfaces;
using FinTrackPrime.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTrackPrime.WebApi.Controllers
{
    [ApiController]
    [Route("api/loan-calculator")]
    [Authorize(Policy = "RequireLoanCalculator")]
    public class LoanCalculatorController : ControllerBase
    {
        private readonly ILoanCalculatorService _loanCalculatorService;

        public LoanCalculatorController(ILoanCalculatorService loanCalculatorService)
        {
            _loanCalculatorService = loanCalculatorService;
        }

        // Stateless: nothing about a calculation is tied to the user or
        // saved, so this is a POST-as-compute rather than a resource.
        [HttpPost("calculate")]
        public ActionResult<LoanCalculationResultViewModel> Calculate(LoanCalculationRequest request)
        {
            var result = _loanCalculatorService.Calculate(request);
            return Ok(result);
        }
    }
}
