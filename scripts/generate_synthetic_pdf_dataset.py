#!/usr/bin/env python3
"""Generate synthetic resume and JD PDFs for screening system testing.

No third-party dependencies required.
"""

from __future__ import annotations

import argparse
import csv
import random
import textwrap
from dataclasses import dataclass
from pathlib import Path


FIRST_NAMES = [
    "Aarav", "Aanya", "Neha", "Rahul", "Vikram", "Priya", "Ananya", "Karan", "Riya", "Dev",
    "Sneha", "Ishaan", "Meera", "Rohan", "Kavya", "Arjun", "Nisha", "Sanjay", "Pooja", "Varun",
    "Nikhil", "Shreya", "Tara", "Aditya", "Nandini", "Soham", "Maya", "Ritika", "Harsh", "Zoya",
]

LAST_NAMES = [
    "Sharma", "Patel", "Reddy", "Gupta", "Singh", "Iyer", "Nair", "Khan", "Joshi", "Mehta",
    "Desai", "Roy", "Bose", "Kapoor", "Chopra", "Mishra", "Saxena", "Kulkarni", "Verma", "Das",
]

CITIES = [
    "Bengaluru", "Hyderabad", "Pune", "Chennai", "Mumbai", "Delhi", "Kolkata", "Ahmedabad", "Noida", "Kochi",
]

DEGREES = [
    "B.Tech Computer Science", "B.E. Information Technology", "MCA", "M.Tech Software Engineering",
    "B.Sc Computer Applications", "B.Tech Electronics", "B.Sc Statistics", "MBA Analytics",
]

CERTIFICATIONS = [
    "AWS Certified Developer", "Azure Fundamentals", "Google Cloud Associate Engineer",
    "Scrum Master Certified", "ISTQB Foundation", "Databricks Lakehouse Fundamentals",
    "Oracle Java SE Programmer", "Tableau Desktop Specialist", "Microsoft Power BI Data Analyst",
]

ROLES = [
    "Backend Developer", "Frontend Developer", "Full Stack Engineer", "Data Engineer", "Data Analyst",
    "ML Engineer", "DevOps Engineer", "QA Automation Engineer", "Product Analyst", "Cloud Engineer",
    "Security Engineer", "Mobile App Developer", "SRE Engineer", "BI Developer", "NLP Engineer",
]

ROLE_SKILLS = {
    "Backend Developer": ["C#", ".NET", "ASP.NET Core", "SQL", "REST APIs", "Redis", "Docker", "Microservices"],
    "Frontend Developer": ["React", "TypeScript", "JavaScript", "HTML", "CSS", "Redux", "Next.js", "Jest"],
    "Full Stack Engineer": ["Node.js", "React", "TypeScript", "PostgreSQL", "GraphQL", "Docker", "CI/CD", "AWS"],
    "Data Engineer": ["Python", "SQL", "Spark", "Airflow", "ETL", "Kafka", "Databricks", "Snowflake"],
    "Data Analyst": ["SQL", "Python", "Power BI", "Tableau", "A/B Testing", "Excel", "Looker", "Statistics"],
    "ML Engineer": ["Python", "PyTorch", "TensorFlow", "Feature Engineering", "MLflow", "FastAPI", "Docker", "MLOps"],
    "DevOps Engineer": ["Linux", "Docker", "Kubernetes", "Terraform", "AWS", "GitHub Actions", "Prometheus", "Grafana"],
    "QA Automation Engineer": ["Selenium", "Playwright", "Cypress", "Java", "Python", "API Testing", "JMeter", "CI/CD"],
    "Product Analyst": ["SQL", "Python", "Mixpanel", "Amplitude", "Experimentation", "Storytelling", "Funnel Analysis", "Excel"],
    "Cloud Engineer": ["AWS", "Azure", "GCP", "Terraform", "Kubernetes", "Networking", "IAM", "Cost Optimization"],
    "Security Engineer": ["SIEM", "SOC", "Threat Modeling", "IAM", "OWASP", "Vulnerability Management", "Python", "Cloud Security"],
    "Mobile App Developer": ["Kotlin", "Swift", "Flutter", "React Native", "REST APIs", "Firebase", "Unit Testing", "CI/CD"],
    "SRE Engineer": ["Linux", "Kubernetes", "Monitoring", "Incident Response", "SLO", "Terraform", "Python", "On-call"],
    "BI Developer": ["SQL", "Power BI", "Tableau", "DAX", "Data Modeling", "ETL", "Stakeholder Management", "Data Warehouse"],
    "NLP Engineer": ["Python", "Transformers", "LLMs", "Prompt Engineering", "Vector DB", "RAG", "FastAPI", "Evaluation"],
}


@dataclass
class Candidate:
    candidate_id: str
    full_name: str
    role: str
    years: int
    city: str
    email: str
    phone: str
    degree: str
    skills: list[str]
    certs: list[str]


def _pdf_escape(text: str) -> str:
    return text.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def write_simple_pdf(path: Path, lines: list[str]) -> None:
    """Write a minimal one-page PDF with Helvetica text."""

    wrapped: list[str] = []
    for line in lines:
        if not line:
            wrapped.append("")
            continue
        wrapped.extend(textwrap.wrap(line, width=95) or [""])

    max_lines = 52
    if len(wrapped) > max_lines:
        wrapped = wrapped[: max_lines - 1] + ["... (truncated)"]

    content_parts = ["BT", "/F1 11 Tf", "50 790 Td", "13 TL"]
    first = True
    for line in wrapped:
        safe = _pdf_escape(line)
        if first:
            content_parts.append(f"({safe}) Tj")
            first = False
        else:
            content_parts.append("T*")
            content_parts.append(f"({safe}) Tj")
    content_parts.append("ET")

    stream = "\n".join(content_parts).encode("latin-1", errors="replace")

    objects: list[bytes] = []
    objects.append(b"1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n")
    objects.append(b"2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n")
    objects.append(
        b"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n"
    )
    objects.append(
        f"4 0 obj\n<< /Length {len(stream)} >>\nstream\n".encode("ascii")
        + stream
        + b"\nendstream\nendobj\n"
    )
    objects.append(b"5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n")

    pdf = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = [0]
    for obj in objects:
        offsets.append(len(pdf))
        pdf.extend(obj)

    xref_start = len(pdf)
    pdf.extend(f"xref\n0 {len(objects)+1}\n".encode("ascii"))
    pdf.extend(b"0000000000 65535 f \n")
    for off in offsets[1:]:
        pdf.extend(f"{off:010d} 00000 n \n".encode("ascii"))

    pdf.extend(
        f"trailer\n<< /Size {len(objects)+1} /Root 1 0 R >>\nstartxref\n{xref_start}\n%%EOF\n".encode(
            "ascii"
        )
    )

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(pdf)


def random_phone(rng: random.Random) -> str:
    return f"+91-{rng.randint(70000, 99999)}-{rng.randint(10000, 99999)}"


def make_candidate(idx: int, rng: random.Random) -> Candidate:
    first = rng.choice(FIRST_NAMES)
    last = rng.choice(LAST_NAMES)
    role = rng.choice(ROLES)
    years = rng.randint(1, 14)
    city = rng.choice(CITIES)
    email = f"{first.lower()}.{last.lower()}{idx}@example.com"
    degree = rng.choice(DEGREES)
    role_skills = ROLE_SKILLS[role][:]
    rng.shuffle(role_skills)
    skill_count = rng.randint(6, 8)
    skills = role_skills[:skill_count]
    cert_count = rng.randint(1, 3)
    certs = rng.sample(CERTIFICATIONS, k=cert_count)

    return Candidate(
        candidate_id=f"CAND{idx:04d}",
        full_name=f"{first} {last}",
        role=role,
        years=years,
        city=city,
        email=email,
        phone=random_phone(rng),
        degree=degree,
        skills=skills,
        certs=certs,
    )


def resume_lines(c: Candidate, rng: random.Random) -> list[str]:
    companies = ["TechNova", "DataBridge", "CloudAxis", "Finstack", "PixelWorks", "QuantHive", "BlueOrbit", "Innotech"]
    domains = ["fintech", "healthtech", "ecommerce", "SaaS", "edtech", "logistics", "media", "enterprise"]
    metrics = [
        f"improved API latency by {rng.randint(18, 52)}%",
        f"reduced cloud cost by {rng.randint(12, 38)}%",
        f"increased deployment frequency by {rng.randint(2, 7)}x",
        f"cut incident volume by {rng.randint(15, 45)}%",
        f"improved data freshness from daily to {rng.choice(['hourly', '15 minutes', 'near real-time'])}",
        f"increased dashboard adoption by {rng.randint(20, 70)}%",
    ]

    company1 = rng.choice(companies)
    company2 = rng.choice([c for c in companies if c != company1])
    domain1 = rng.choice(domains)
    domain2 = rng.choice([d for d in domains if d != domain1])

    lines = [
        c.full_name,
        f"Target Role: {c.role}",
        f"Location: {c.city} | Email: {c.email} | Phone: {c.phone}",
        "",
        "Professional Summary",
        f"{c.years}+ years experience in {c.role.lower()} projects across {domain1} and {domain2}.",
        f"Delivered measurable outcomes and collaborated with product, design, and QA teams.",
        "",
        "Core Skills",
        ", ".join(c.skills),
        "",
        "Experience",
        f"{c.role} - {company1} (2022-2026)",
        f"- Led a cross-functional squad and {rng.choice(metrics)}.",
        f"- Built and maintained production systems using {', '.join(c.skills[:4])}.",
        f"- Mentored junior engineers and improved code review quality.",
        f"{c.role} - {company2} (2019-2022)",
        f"- Implemented features for {domain1} platform and {rng.choice(metrics)}.",
        f"- Partnered with stakeholders to define quarterly delivery plans.",
        "",
        "Education",
        f"{c.degree} - {rng.choice(['Anna University', 'VTU', 'JNTU', 'Mumbai University', 'Delhi University'])}",
        "",
        "Certifications",
        ", ".join(c.certs),
        "",
        "Notice Period",
        f"{rng.choice(['Immediate', '15 days', '30 days', '45 days', '60 days'])}",
    ]
    return lines


def jd_lines(jd_id: int, role: str, rng: random.Random) -> list[str]:
    skills = ROLE_SKILLS[role][:]
    rng.shuffle(skills)
    must = skills[:5]
    good = skills[5:8]
    exp = rng.randint(2, 10)
    city = rng.choice(CITIES)
    employment = rng.choice(["Full-time", "Contract", "Hybrid", "Remote"])

    lines = [
        f"Job Description - {role}",
        f"Job ID: JD{jd_id:04d}",
        f"Location: {city} | Mode: {employment}",
        f"Experience Required: {exp}+ years",
        "",
        "Role Overview",
        f"We are hiring a {role} to design, build, and scale reliable products for business-critical workflows.",
        "",
        "Key Responsibilities",
        "- Build and deliver high quality features aligned to product roadmaps.",
        "- Collaborate with cross-functional teams to define and ship solutions.",
        "- Improve system quality through monitoring, testing, and automation.",
        "- Communicate risks, trade-offs, and delivery timelines clearly.",
        "",
        "Must-Have Skills",
        "- " + "\n- ".join(must),
        "",
        "Good-to-Have Skills",
        "- " + "\n- ".join(good),
        "",
        "Screening Criteria",
        f"- Relevant experience in {role.lower()} and strong fundamentals.",
        "- Demonstrated ownership and clear communication.",
        "- Practical problem-solving with measurable impact in previous roles.",
        "",
        "Compensation",
        f"Indicative CTC range: INR {rng.randint(8, 22)}-{rng.randint(23, 55)} LPA (based on fit and interview performance)",
    ]
    return lines


def generate(output_root: Path, resume_count: int, jd_count: int, seed: int) -> None:
    rng = random.Random(seed)

    resumes_dir = output_root / "resumes"
    jds_dir = output_root / "jds"
    resumes_dir.mkdir(parents=True, exist_ok=True)
    jds_dir.mkdir(parents=True, exist_ok=True)

    resume_manifest = output_root / "resumes_manifest.csv"
    jd_manifest = output_root / "jds_manifest.csv"

    with resume_manifest.open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["candidate_id", "name", "role", "years_experience", "location", "resume_pdf"])
        for i in range(1, resume_count + 1):
            cand = make_candidate(i, rng)
            lines = resume_lines(cand, rng)
            filename = f"resume_{cand.candidate_id}_{cand.role.lower().replace(' ', '_')}.pdf"
            path = resumes_dir / filename
            write_simple_pdf(path, lines)
            writer.writerow([cand.candidate_id, cand.full_name, cand.role, cand.years, cand.city, str(path)])

    with jd_manifest.open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["job_id", "role", "location", "experience_required", "jd_pdf"])
        for i in range(1, jd_count + 1):
            role = rng.choice(ROLES)
            lines = jd_lines(i, role, rng)
            exp_txt = next((ln.split(": ", 1)[1] for ln in lines if ln.startswith("Experience Required:")), "N/A")
            loc_txt = next((ln.split(": ", 1)[1].split(" | ", 1)[0] for ln in lines if ln.startswith("Location:")), "N/A")
            filename = f"jd_JD{i:04d}_{role.lower().replace(' ', '_')}.pdf"
            path = jds_dir / filename
            write_simple_pdf(path, lines)
            writer.writerow([f"JD{i:04d}", role, loc_txt, exp_txt, str(path)])

    readme = output_root / "README.md"
    readme.write_text(
        "\n".join(
            [
                "# Synthetic Resume and JD PDF Dataset",
                "",
                "This folder contains synthetic documents for testing resume screening workflows.",
                "",
                f"- Resumes generated: {resume_count}",
                f"- JDs generated: {jd_count}",
                f"- Random seed: {seed}",
                "",
                "## Structure",
                "",
                "- resumes/: candidate resume PDFs",
                "- jds/: job description PDFs",
                "- resumes_manifest.csv: candidate metadata",
                "- jds_manifest.csv: job metadata",
            ]
        ),
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate synthetic resume and JD PDF files")
    parser.add_argument("--output", type=Path, default=Path("output/pdf/synthetic_screening_dataset"))
    parser.add_argument("--resumes", type=int, default=120)
    parser.add_argument("--jds", type=int, default=30)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()

    if args.resumes < 100:
        raise SystemExit("Please request at least 100 resumes.")
    if args.jds < 1:
        raise SystemExit("Please request at least 1 JD.")

    generate(args.output, args.resumes, args.jds, args.seed)
    print(f"Generated {args.resumes} resumes and {args.jds} JDs under {args.output}")


if __name__ == "__main__":
    main()
