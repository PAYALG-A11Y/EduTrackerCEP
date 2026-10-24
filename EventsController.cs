
using EduTrack.Data;
using EduTrack.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text;

namespace EduTrack.Controllers;

[Authorize]
public class EventsController : Controller
{
    private readonly ApplicationDbContext _db;

    public EventsController(ApplicationDbContext db)
    {
        _db = db;
    }

    private IQueryable<Classroom> MyClasses()
    {
        var userId = User.FindFirstValue(
            ClaimTypes.NameIdentifier);

        if (User.IsInRole("Admin"))
            return _db.Classrooms;

        return _db.Classrooms.Where(
            c => c.TeacherId == userId);
    }

    // Display events
    public async Task<IActionResult> Index()
    {
        var classes = await MyClasses()
            .Select(c => c.Id)
            .ToListAsync();

        var events = await _db.Events
            .Where(e => classes.Contains(e.ClassroomId))
            .Include(e => e.Classroom)
            .OrderByDescending(e => e.Date)
            .ToListAsync();

        return View(events);
    }

    // Display event creation page
    public async Task<IActionResult> Create()
    {
        await LoadClasses();
        return View();
    }

    // Create event
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Event model)
    {
        var classroom = await MyClasses()
            .FirstOrDefaultAsync(
                c => c.Id == model.ClassroomId);

        if (classroom == null)
        {
            ModelState.AddModelError(
                "ClassroomId",
                "Please select a valid class.");
        }

        if (ModelState.IsValid)
        {
            model.CreatedById =
                User.FindFirstValue(
                    ClaimTypes.NameIdentifier) ?? "";

            _db.Events.Add(model);
            await _db.SaveChangesAsync();

            TempData["Success"] =
                "Event created successfully.";

            return RedirectToAction(nameof(Index));
        }

        await LoadClasses();
        return View(model);
    }

    // Display event attendance
    public async Task<IActionResult> Attendance(int id)
    {
        var ev = await _db.Events
            .Include(e => e.Classroom)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (ev == null)
            return NotFound();

        var allowed = await MyClasses()
            .AnyAsync(c => c.Id == ev.ClassroomId);

        if (!allowed)
            return Forbid();

        var students = await _db.Students
            .Where(s => s.ClassroomId == ev.ClassroomId)
            .OrderBy(s => s.RollNumber)
            .ToListAsync();

        var participations = await _db.EventParticipations
            .Where(p => p.EventId == id)
            .ToDictionaryAsync(
                p => p.StudentId,
                p => p.IsPresent);

        ViewBag.Event = ev;
        ViewBag.Participations = participations;

        return View(students);
    }

    // Save event attendance
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Attendance(
        int id, int[] presentStudentIds)
    {
        var ev = await _db.Events
            .FirstOrDefaultAsync(e => e.Id == id);

        if (ev == null)
            return NotFound();

        var allowed = await MyClasses()
            .AnyAsync(c => c.Id == ev.ClassroomId);

        if (!allowed)
            return Forbid();

        presentStudentIds ??= Array.Empty<int>();

        var studentIds = await _db.Students
            .Where(s => s.ClassroomId == ev.ClassroomId)
            .Select(s => s.Id)
            .ToListAsync();

        var existing = await _db.EventParticipations
            .Where(p => p.EventId == id)
            .ToListAsync();

        foreach (var studentId in studentIds)
        {
            var participation = existing
                .FirstOrDefault(
                    p => p.StudentId == studentId);

            var isPresent =
                presentStudentIds.Contains(studentId);

            if (participation == null)
            {
                _db.EventParticipations.Add(
                    new EventParticipation
                    {
                        EventId = id,
                        StudentId = studentId,
                        IsPresent = isPresent
                    });
            }
            else
            {
                participation.IsPresent = isPresent;
            }
        }

        await _db.SaveChangesAsync();

        TempData["Success"] =
            "Event attendance saved.";

        return RedirectToAction(nameof(Index));
    }

    // Download event attendance as CSV
    public async Task<IActionResult> DownloadAttendanceCsv(int id)
    {
        var ev = await _db.Events
            .Include(e => e.Classroom)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (ev == null)
            return NotFound();

        // Verify that the current user can access this class
        var allowed = await MyClasses()
            .AnyAsync(c => c.Id == ev.ClassroomId);

        if (!allowed)
            return Forbid();

        // Get all students in the event's class
        var students = await _db.Students
            .Where(s => s.ClassroomId == ev.ClassroomId)
            .OrderBy(s => s.RollNumber)
            .ToListAsync();

        // Get saved attendance for this event
        var participations = await _db.EventParticipations
            .Where(p => p.EventId == id)
            .ToDictionaryAsync(
                p => p.StudentId,
                p => p.IsPresent);

        var csv = new StringBuilder();

        // CSV headers
        csv.AppendLine(
            "Event Name,Event Date,Class,Roll Number,Student Name,Attendance");

        foreach (var student in students)
        {
            bool isPresent =
                participations.TryGetValue(
                    student.Id, out var status) && status;

            var values = new[]
            {
                CsvEscape(ev.Name),
                CsvEscape(ev.Date.ToString("dd MMM yyyy")),
                CsvEscape(ev.Classroom?.Name ?? ""),
                CsvEscape(Convert.ToString(student.RollNumber) ?? ""),
                CsvEscape(student.FullName ?? ""),
                CsvEscape(isPresent ? "Present" : "Absent")
            };

            csv.AppendLine(string.Join(",", values));
        }

        // UTF-8 BOM supports special characters in Excel
        var encoding = new UTF8Encoding(true);

        var bytes = encoding.GetPreamble()
            .Concat(encoding.GetBytes(csv.ToString()))
            .ToArray();

        return File(
            bytes,
            "text/csv; charset=utf-8",
            $"EventAttendance_{ev.Id}.csv");
    }

    // Escape CSV values containing commas, quotes or newlines
    private static string CsvEscape(string? value)
    {
        value ??= "";

        // Prevent spreadsheet formula injection for text fields
        if (value.Length > 0 &&
            (value[0] == '=' ||
             value[0] == '+' ||
             value[0] == '-' ||
             value[0] == '@' ||
             value[0] == '\t' ||
             value[0] == '\r'))
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    // Load classes for event creation
    private async Task LoadClasses()
    {
        var classes = await MyClasses()
            .OrderBy(c => c.Name)
            .ToListAsync();

        ViewBag.Classrooms = new SelectList(
            classes, "Id", "Name");
    }
}
