using BookHiveLibrary.Data;
using BookHiveLibrary.Models;
using BookHiveLibrary.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using Microsoft.AspNetCore.Authorization;
using QRCoder;

namespace BookHiveLibrary.Controllers
{
    [Authorize(Roles = "LIBRARIAN,MIS")]
    public class BookController : Controller
    {
        private readonly ApplicationDbContext _context;

        public BookController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Library Catalog
        public async Task<IActionResult> Index(string? search, string? category)
        {
            var query = _context.Books.Where(b => !b.IsArchived);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(b =>
                    b.Title.Contains(search) ||
                    b.Author.Contains(search) ||
                    b.CallNumber.Contains(search));

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(b => b.Category == category);

            var books = await query.OrderBy(b => b.Title).ToListAsync();

            ViewBag.Search = search;
            ViewBag.Category = category;
            ViewBag.Categories = await _context.Books
                .Where(b => !b.IsArchived)
                .Select(b => b.Category)
                .Distinct()
                .OrderBy(c => c)
                .ToListAsync();

            return View(books);
        }

        // Register / Add Book
        public async Task<IActionResult> Register()
        {
            ViewBag.TotalBooks = await _context.Books.SumAsync(b => (int?)b.TotalQuantity) ?? 0;
            ViewBag.AvailableBooks = await _context.Books.Where(b => !b.IsArchived).SumAsync(b => (int?)b.AvailableQuantity) ?? 0;
            ViewBag.BorrowedBooks = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp");
            ViewBag.ArchivedBooks = await _context.Books.CountAsync(b => b.IsArchived);
            return View(new BookFormViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(BookFormViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            _context.Books.Add(new Book
            {
                Title = model.Title,
                Author = model.Author,
                Category = model.Category,
                GradeLevel = model.GradeLevel,
                CallNumber = model.CallNumber,
                AisleLocation = model.AisleLocation,
                Description = model.Description,
                CoverImageUrl = model.CoverImageUrl,
                PublishedYear = model.PublishedYear,
                ISBN = model.ISBN,
                TotalQuantity = model.TotalQuantity,
                AvailableQuantity = model.TotalQuantity,
                BookFor = model.BookFor,
                IsRoomUseOnly = model.IsRoomUseOnly
            });

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book registered successfully.";
            return RedirectToAction("Index");
        }

        // Edit Book
        public async Task<IActionResult> Edit(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            return View(new BookFormViewModel
            {
                Id = book.Id,
                Title = book.Title,
                Author = book.Author,
                Category = book.Category,
                GradeLevel = book.GradeLevel,
                CallNumber = book.CallNumber,
                AisleLocation = book.AisleLocation,
                Description = book.Description,
                CoverImageUrl = book.CoverImageUrl,
                PublishedYear = book.PublishedYear,
                TotalQuantity = book.TotalQuantity,
                ISBN = book.ISBN,
                AvailableQuantity = book.AvailableQuantity,
                BookFor = book.BookFor,
                IsRoomUseOnly = book.IsRoomUseOnly
            });
        }

        [HttpPost]
        public async Task<IActionResult> Edit(BookFormViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            var book = await _context.Books.FindAsync(model.Id);
            if (book == null) return NotFound();

            book.Title = model.Title;
            book.Author = model.Author;
            book.Category = model.Category;
            book.GradeLevel = model.GradeLevel;
            book.CallNumber = model.CallNumber;
            book.AisleLocation = model.AisleLocation;
            book.Description = model.Description;
            book.CoverImageUrl = model.CoverImageUrl;
            book.PublishedYear = model.PublishedYear;
            book.ISBN = model.ISBN;
            book.BookFor = model.BookFor;
            // Calculate available based on actual active borrows, not stored AvailableQuantity
            var actualBorrowed = await _context.BookReservations
                .CountAsync(r => r.BookId == book.Id && r.Status == "PickedUp");
            book.TotalQuantity = model.TotalQuantity;
            book.AvailableQuantity = Math.Max(0, model.TotalQuantity - actualBorrowed);
            book.IsRoomUseOnly = model.IsRoomUseOnly;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book updated successfully.";
            return RedirectToAction("Index");
        }

        // Import books from Excel
        [HttpPost]
        public async Task<IActionResult> ImportExcel(IFormFile file)
        {
            if (file == null || file.Length == 0)
            {
                TempData["Error"] = "Please select an Excel file.";
                return RedirectToAction("Register");
            }

            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            var importedIds = new List<int>();
            var errors = new List<string>();

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            using var package = new ExcelPackage(stream);

            var ws = package.Workbook.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                TempData["Error"] = "Excel file has no worksheets.";
                return RedirectToAction("Register");
            }

            // Row 1 = header, data starts row 2
            for (int row = 2; row <= ws.Dimension?.End.Row; row++)
            {
                var title = ws.Cells[row, 1].Text.Trim();
                if (string.IsNullOrEmpty(title)) continue;

                var book = new Book
                {
                    Title         = title,
                    Author        = ws.Cells[row, 2].Text.Trim(),
                    Category      = ws.Cells[row, 3].Text.Trim(),
                    GradeLevel    = ws.Cells[row, 4].Text.Trim(),
                    CallNumber    = ws.Cells[row, 5].Text.Trim(),
                    AisleLocation = ws.Cells[row, 6].Text.Trim(),
                    PublishedYear = ws.Cells[row, 7].Text.Trim(),
                    TotalQuantity     = int.TryParse(ws.Cells[row, 8].Text.Trim(), out var qty) ? qty : 1,
                    AvailableQuantity = int.TryParse(ws.Cells[row, 8].Text.Trim(), out var qty2) ? qty2 : 1,
                    ISBN          = ws.Cells[row, 9].Text.Trim(),
                };

                _context.Books.Add(book);
                await _context.SaveChangesAsync();
                importedIds.Add(book.Id);
            }

            if (!importedIds.Any())
            {
                TempData["Error"] = "No valid rows found. Make sure row 1 is a header and data starts on row 2.";
                return RedirectToAction("Register");
            }

            TempData["ImportedIds"] = string.Join(",", importedIds);
            TempData["Success"] = $"{importedIds.Count} book(s) imported. Review and complete the details below.";
            return RedirectToAction("ImportReview");
        }

        // Review table after import
        public async Task<IActionResult> ImportReview()
        {
            var idString = TempData["ImportedIds"] as string ?? "";
            if (string.IsNullOrEmpty(idString))
                return RedirectToAction("Index");

            // Keep TempData alive for page refreshes
            TempData.Keep("ImportedIds");

            var ids = idString.Split(',').Select(int.Parse).ToList();
            var books = await _context.Books
                .Where(b => ids.Contains(b.Id))
                .OrderBy(b => b.Title)
                .ToListAsync();

            return View(books);
        }

        // AJAX: save cover + synopsis for one book during review
        [HttpPost]
        public async Task<IActionResult> UpdateBookDetails(int id, string? coverImageUrl, string? description)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            book.CoverImageUrl = coverImageUrl ?? "";
            book.Description   = description   ?? "";
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // Print Label (single)
        public async Task<IActionResult> PrintLabel(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();
            return View(book);
        }

        // Print Labels (bulk)
        public async Task<IActionResult> PrintLabels(string ids)
        {
            if (string.IsNullOrEmpty(ids)) return BadRequest();
            var idList = ids.Split(',').Select(s => int.TryParse(s.Trim(), out var n) ? n : 0).Where(n => n > 0).ToList();
            var books = await _context.Books.Where(b => idList.Contains(b.Id)).ToListAsync();
            return View(books);
        }

        // Archive Book
        [HttpPost]
        public async Task<IActionResult> Archive(int id, string reason)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            book.IsArchived = true;
            book.ArchiveReason = reason;
            book.AvailableQuantity = 0;

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book archived.";
            return RedirectToAction("Index");
        }

        // Archived Books List
        public async Task<IActionResult> Archive()
        {
            ViewBag.TotalBooks = await _context.Books.SumAsync(b => (int?)b.TotalQuantity) ?? 0;
            ViewBag.AvailableBooks = await _context.Books.Where(b => !b.IsArchived).SumAsync(b => (int?)b.AvailableQuantity) ?? 0;
            ViewBag.BorrowedBooks = await _context.BookReservations.CountAsync(r => r.Status == "PickedUp");
            ViewBag.ArchivedBooks = await _context.Books.CountAsync(b => b.IsArchived);

            var archived = await _context.Books
                .Where(b => b.IsArchived)
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync();
            return View(archived);
        }

        // QR Code image for shelf location
        public async Task<IActionResult> QRCode(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            string content = $"BookHive\nTitle: {book.Title}\nCall No: {book.CallNumber}\nAisle: {book.AisleLocation}";

            using var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
            using var qrCode = new PngByteQRCode(qrData);
            byte[] png = qrCode.GetGraphic(6);

            return File(png, "image/png");
        }

        // Restore from Archive
        [HttpPost]
        public async Task<IActionResult> Restore(int id)
        {
            var book = await _context.Books.FindAsync(id);
            if (book == null) return NotFound();

            book.IsArchived = false;
            book.ArchiveReason = "";

            await _context.SaveChangesAsync();
            TempData["Success"] = "Book restored.";
            return RedirectToAction("Archive");
        }
    }
}
