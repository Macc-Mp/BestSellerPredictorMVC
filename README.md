# 📈 BestSellerPredictorMVC

A high-performance prediction tool built with **LightGBM** and the **.NET ML.NET library**. This application analyzes product sales data to classify and predict future market performance.

---

## 🚀 Key Features

* **Machine Learning Powered:** Uses advanced LightGBM classification algorithms for high-accuracy predictions.
* **Data-Driven:** Leverages historical sales data to forecast trends.
* **Clean Architecture:** Built with an MVC (Model-View-Controller) design for scalability and maintainability.
* **Simple Workflow:** Just upload your Excel file and receive actionable insights.

---

## 📊 Data Requirements

To ensure accurate result generation, your Excel file must follow the required format:

| Column Name | Type | Description |
| :--- | :--- | :--- |
| **ProductID** | `string` | Date of the sales record |
| **ProductName** | `string` | Unique identifier for the product |
| **Category** | `string` | Product department or group |
| **UnitPrice** | `decimal` | Price |
| **QuanitySold** | `int` | volume of product sold |

---

## 🛠 Tech Stack

* **Framework:** ASP.NET Core MVC
* **ML Engine:** Microsoft ML.NET
* **Algorithm:** LightGBM
* **Language:** C#

---

## ⚖️ License

This project is licensed under the **CC BY-NC 4.0** license. 
* **You are free to:** Share and adapt the material for non-commercial purposes.
* **You must:** Give appropriate credit and indicate if changes were made.
* **Commercial use:** Strictly prohibited. 

See the `LICENSE` file for full legal details.

---

## 💡 How to Get Started

1. **Clone the repo:** `git clone https://github.com/your-username/BestSellerPredictorMVC.git`
2. **Build the project** in your favorite IDE (Visual Studio / VS Code).
3. **Upload** your formatted Excel file through the application interface.
4. **View results** to optimize your inventory or marketing strategies!

---
*Built with ❤️ using .NET*
