namespace gcbulkgrader.Models
{
    public class AssignmentGrade
    {
        public string CourseId { get; set; }
        public string CourseWorkId { get; set; }
        public string SubmissionId { get; set; }
        public string StudentId { get; set; }
        public double? AssignedGrade { get; set; }
    }
}
